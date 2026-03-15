using System;
using System.Collections.Generic;
using StrongestVwap.Core.Models;

namespace StrongestVwap.Strategy
{
    /// <summary>
    /// Entry execution: pre-entry filters, position sizing, take-profit order placement.
    /// Mode E: TP based on entry price with custom share ratios.
    /// </summary>
    public class OrderTrigger
    {
        private readonly double _positionCash;
        private readonly bool _dispositionEnabled;
        private readonly bool _filterPrevDayLimitUp;
        private readonly double _maxEntryPrice;
        private readonly TimeSpan _entryTimeLimit;
        private readonly int _takeProfitSplits;
        private readonly List<double> _takeProfitPcts;
        private readonly int _reserveLimitUpSplits;

        // Mode E: entry-price based TP with custom share ratios
        private readonly bool _modeEExit;
        private readonly List<double> _takeProfitRatios;
        private readonly double _reserveLimitUpRatio;

        // Rolling low exit: no TP orders needed
        private readonly bool _rollingLowEnabled;

        public OrderTrigger(StrategyConfig config)
        {
            _positionCash = config.GetDouble("position_cash", 10_000_000);
            _dispositionEnabled = config.GetBool("disposition_stocks_enabled", true);
            _filterPrevDayLimitUp = config.GetBool("filter_prev_day_limit_up", true);
            _maxEntryPrice = config.GetDouble("max_entry_price", 1000);
            _entryTimeLimit = config.GetTimeSpan("entry_time_limit", new TimeSpan(13, 0, 0));
            _takeProfitSplits = config.GetInt("take_profit_splits", 3);
            _takeProfitPcts = config.GetDoubleList("take_profit_pcts");
            if (_takeProfitPcts.Count == 0)
                _takeProfitPcts = new List<double> { 0.01, 0.02, 0.03 };
            _reserveLimitUpSplits = config.GetInt("reserve_limit_up_splits", 2);

            _modeEExit = config.GetBool("mode_e_exit", false);
            _takeProfitRatios = config.GetDoubleList("take_profit_ratios");
            _reserveLimitUpRatio = config.GetDouble("reserve_limit_up_ratio", 0.4);
            _rollingLowEnabled = config.GetBool("rolling_low_exit_enabled", false);
        }

        /// <summary>
        /// Attempt to create a TradeRecord for entry. Returns null if any filter blocks.
        /// </summary>
        public TradeRecord? TryEntry(
            string stockId, DateTime time, IndexData idx, MatchInfo matchInfo,
            HashSet<string> currentPositions)
        {
            // Filter 1: entry time limit
            if (time.TimeOfDay >= _entryTimeLimit)
                return null;

            // Filter 2: prev day limit up
            if (_filterPrevDayLimitUp && idx.PrevDayLimitUp)
                return null;

            // Filter 3: disposition stock
            if (_dispositionEnabled && idx.SecurityType == "RR")
                return null;

            // Filter 4: already holding
            if (currentPositions.Contains(stockId))
                return null;

            // Filter 5: max entry price
            double entryPrice = idx.LastPrice;
            if (entryPrice > _maxEntryPrice)
                return null;

            if (entryPrice <= 0)
                return null;

            // Calculate position
            double totalShares = Math.Floor(_positionCash / entryPrice);
            if (totalShares <= 0)
                return null;

            double limitUpPrice = idx.LimitUpPrice > 0
                ? idx.LimitUpPrice
                : TickSizeHelper.CalculateLimitUp(idx.PreviousClose);

            // Create trade record
            var trade = new TradeRecord
            {
                StockId = stockId,
                EntryTime = time,
                EntryPrice = entryPrice,
                EntryVwap = idx.Vwap,
                EntryDayHigh = idx.DayHigh,
                TotalShares = totalShares,
                RemainingShares = totalShares,
                PositionCash = _positionCash,
                EntryGroupName = matchInfo.GroupName,
                EntryGroupRank = matchInfo.GroupRank,
                EntryTotalMemberRank = matchInfo.TotalMemberRank,
                EntryMemberRank = matchInfo.MemberRank,
                EntryGroupMembers = matchInfo.GroupMembers
            };

            if (_rollingLowEnabled)
            {
                // Rolling low mode: no TP orders — exit handled by ExitManager
            }
            else if (_modeEExit)
                PlaceModeETakeProfitOrders(trade, entryPrice, limitUpPrice, totalShares);
            else
                PlaceOriginalTakeProfitOrders(trade, idx.DayHigh, limitUpPrice, totalShares);

            return trade;
        }

        /// <summary>
        /// Mode E: TP based on entry price with custom share ratios (20%/20%/20%/40%).
        /// </summary>
        private void PlaceModeETakeProfitOrders(TradeRecord trade, double entryPrice,
            double limitUpPrice, double totalShares)
        {
            double sharesPlaced = 0;

            // TP orders based on entry price
            for (int i = 0; i < _takeProfitPcts.Count; i++)
            {
                double pct = _takeProfitPcts[i];
                double ratio = i < _takeProfitRatios.Count ? _takeProfitRatios[i] : 0.2;
                double shares = Math.Floor(totalShares * ratio);

                double rawPrice = entryPrice * (1.0 + pct);
                double tpPrice = TickSizeHelper.CeilToTick(rawPrice);

                // Cap at limit-up
                if (limitUpPrice > 0 && tpPrice > limitUpPrice)
                    tpPrice = limitUpPrice;

                trade.TakeProfitOrders.Add(new TakeProfitOrder
                {
                    Index = i,
                    TargetPrice = tpPrice,
                    Shares = shares,
                    Type = "takeProfit"
                });
                sharesPlaced += shares;
            }

            // Limit-up order gets the remainder
            double limitUpShares = totalShares - sharesPlaced;
            if (limitUpShares > 0 && limitUpPrice > 0)
            {
                trade.TakeProfitOrders.Add(new TakeProfitOrder
                {
                    Index = _takeProfitPcts.Count,
                    TargetPrice = limitUpPrice,
                    Shares = limitUpShares,
                    Type = "limitUp"
                });
            }
        }

        /// <summary>
        /// Original mode: TP based on day high with equal splits.
        /// </summary>
        private void PlaceOriginalTakeProfitOrders(TradeRecord trade, double dayHigh,
            double limitUpPrice, double totalShares)
        {
            int totalSplits = _takeProfitSplits + _reserveLimitUpSplits;
            double sharesPerSplit = Math.Floor(totalShares / totalSplits);

            for (int i = 0; i < _takeProfitSplits; i++)
            {
                double pct = i < _takeProfitPcts.Count ? _takeProfitPcts[i] : 0.03;
                double rawPrice = dayHigh * (1.0 + pct);
                double tpPrice = TickSizeHelper.CeilToTick(rawPrice);

                // Cap at limit-up
                if (limitUpPrice > 0 && tpPrice > limitUpPrice)
                    tpPrice = limitUpPrice;

                trade.TakeProfitOrders.Add(new TakeProfitOrder
                {
                    Index = i,
                    TargetPrice = tpPrice,
                    Shares = sharesPerSplit,
                    Type = "takeProfit"
                });
            }

            for (int i = 0; i < _reserveLimitUpSplits; i++)
            {
                double shares = (i == _reserveLimitUpSplits - 1)
                    ? totalShares - sharesPerSplit * (totalSplits - 1)
                    : sharesPerSplit;

                trade.TakeProfitOrders.Add(new TakeProfitOrder
                {
                    Index = _takeProfitSplits + i,
                    TargetPrice = limitUpPrice,
                    Shares = shares,
                    Type = "limitUp"
                });
            }
        }
    }
}
