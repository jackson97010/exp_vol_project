using System;
using System.Collections.Generic;
using StrongestVwap.Core.Models;
using StrongestVwap.Strategy;

namespace StrongestVwap.Core
{
    /// <summary>
    /// Main tick-by-tick loop processing all stocks simultaneously.
    /// Flow per tick: market filter -> update IndexData -> exit check -> strong group -> signal A -> entry
    /// </summary>
    public class BacktestLoop
    {
        private readonly StrategyConfig _config;
        private readonly MarketFilter _marketFilter;
        private readonly StrongGroupScreener _groupScreener;
        private readonly SignalAEvaluator _signalA;
        private readonly OrderTrigger _orderTrigger;
        private readonly ExitManager _exitManager;
        private readonly PositionManager _positionManager;

        // Mode E: direct entry without Signal A
        private readonly bool _signalAEnabled;
        private readonly TimeSpan _entryStartTime;
        private readonly TimeSpan _entryEndTime;

        // Group rank exit: close position if group rank drops below threshold
        private readonly bool _groupRankExitEnabled;
        private readonly int _groupRankExitThreshold;

        // Bypass group screening
        private readonly bool _bypassGroupScreening;

        // Ask > Bid entry condition
        private readonly bool _requireAskGtBid;
        private readonly bool _requireBidGtAsk;

        // Massive matching entry filters
        private readonly bool _massiveMatchingEnabled;
        private readonly int _massiveMatchingWindow;
        private readonly int _massiveMatchingWindowMax; // for tracking (always use max of window and adaptive)
        private readonly bool _adaptiveMmWindowEnabled;
        private readonly double _adaptiveMmWindowThreshold;
        private readonly int _adaptiveMmWindowSeconds;
        private readonly bool _useDynamicLiquidity;
        private readonly bool _intervalPctFilterEnabled;
        private readonly int _intervalPctMinutes;
        private readonly double _intervalPctThreshold;
        private readonly bool _ratioEntryEnabled;
        private readonly double _ratioEntryThreshold;
        private readonly double _askBidRatioThreshold;
        private readonly bool _priceChangeLimitEnabled;
        private readonly double _priceChangeLimitPct;

        // Engine reference for dynamic liquidity lookup
        private readonly BacktestEngine? _engine;
        private readonly string _currentDate;

        // Per-stock IndexData
        private readonly Dictionary<string, IndexData> _allStocks = new();

        // Static data (previous close, monthly avg, etc.)
        private readonly Dictionary<string, StockStaticData> _staticData;

        // Track which stocks have already entered (one entry per stock per day)
        private readonly HashSet<string> _enteredStocks = new();

        public PositionManager PositionManager => _positionManager;

        public BacktestLoop(
            StrategyConfig config,
            Dictionary<string, GroupDefinition> groups,
            Dictionary<string, StockStaticData> staticData,
            string currentDate = "",
            BacktestEngine? engine = null)
        {
            _config = config;
            _staticData = staticData;
            _currentDate = currentDate;
            _engine = engine;

            _signalAEnabled = config.GetBool("signal_a_enabled", true);
            _entryStartTime = config.GetTimeSpan("entry_start_time", new TimeSpan(9, 5, 0));
            _entryEndTime = config.GetTimeSpan("entry_end_time", new TimeSpan(9, 20, 0));

            _groupRankExitEnabled = config.GetBool("group_rank_exit_enabled", false);
            _groupRankExitThreshold = config.GetInt("group_rank_exit_threshold", 3);
            _bypassGroupScreening = config.GetBool("bypass_group_screening", false);
            _requireAskGtBid = config.GetBool("require_ask_gt_bid", false);
            _requireBidGtAsk = config.GetBool("require_bid_gt_ask", false);

            // Massive matching filters
            _massiveMatchingEnabled = config.GetBool("massive_matching_enabled", false);
            _massiveMatchingWindow = config.GetInt("massive_matching_window", 1);
            _adaptiveMmWindowEnabled = config.GetBool("adaptive_mm_window_enabled", false);
            _adaptiveMmWindowThreshold = config.GetDouble("adaptive_mm_window_threshold", 10_000_000);
            _adaptiveMmWindowSeconds = config.GetInt("adaptive_mm_window_seconds", 2);
            _massiveMatchingWindowMax = _adaptiveMmWindowEnabled
                ? Math.Max(_massiveMatchingWindow, _adaptiveMmWindowSeconds)
                : _massiveMatchingWindow;
            _useDynamicLiquidity = config.GetBool("use_dynamic_liquidity_threshold", false);
            _intervalPctFilterEnabled = config.GetBool("interval_pct_filter_enabled", false);
            _intervalPctMinutes = config.GetInt("interval_pct_minutes", 5);
            _intervalPctThreshold = config.GetDouble("interval_pct_threshold", 3.0);
            _ratioEntryEnabled = config.GetBool("ratio_entry_enabled", false);
            _ratioEntryThreshold = config.GetDouble("ratio_entry_threshold", 3.0);
            _askBidRatioThreshold = config.GetDouble("ask_bid_ratio_threshold", 1.0);
            _priceChangeLimitEnabled = config.GetBool("price_change_limit_enabled", false);
            _priceChangeLimitPct = config.GetDouble("price_change_limit_pct", 8.5);

            _marketFilter = new MarketFilter(config);
            _groupScreener = new StrongGroupScreener(config, groups);
            _signalA = new SignalAEvaluator(config);
            _orderTrigger = new OrderTrigger(config);
            _exitManager = new ExitManager(config);
            _positionManager = new PositionManager();

            // Set 0050 prev close
            if (staticData.TryGetValue(Constants.Symbol0050, out var data0050))
                _marketFilter.PrevClose0050 = data0050.PreviousClose;
            else
            {
                // Try to find from any stock's static data that has PrevClose0050
                foreach (var sd in staticData.Values)
                {
                    if (sd.PrevClose0050 > 0)
                    {
                        _marketFilter.PrevClose0050 = sd.PrevClose0050;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Process all ticks for a single day.
        /// </summary>
        public void Run(List<RawTick> ticks)
        {
            Console.WriteLine($"[LOOP] Processing {ticks.Count} ticks...");

            foreach (var tick in ticks)
            {
                ProcessTick(tick);
            }

            // Force close any remaining positions at market close
            if (_positionManager.ActiveCount > 0)
            {
                var closeTime = ticks.Count > 0 ? ticks[^1].Time : DateTime.Now;
                _positionManager.ForceCloseAll(closeTime, _allStocks);
                Console.WriteLine($"[LOOP] Force-closed {_positionManager.ActiveCount} positions at market close");
            }

            Console.WriteLine($"[LOOP] Completed. {_positionManager.CompletedCount} trades.");
        }

        private void ProcessTick(RawTick tick)
        {
            string stockId = tick.StockId;

            // Handle Depth ticks: update order book state only
            if (tick.RowType == "Depth")
            {
                if (tick.BidVolume5Level > 0 || tick.AskVolume5Level > 0)
                {
                    var depthIdx = GetOrCreateIndexData(stockId, tick);
                    depthIdx.BidVolume5Level = tick.BidVolume5Level;
                    depthIdx.AskVolume5Level = tick.AskVolume5Level;
                }
                return;
            }

            // Capture 0050 price for market filter
            if (stockId == Constants.Symbol0050 || stockId.StartsWith("00"))
            {
                if (stockId == Constants.Symbol0050)
                    _marketFilter.UpdateTick(tick.Time, tick.Price);
                return; // Don't process ETFs further
            }

            // Skip non-trade ticks
            if (MarketFilter.ShouldSkipTick(tick.TradeCode, stockId))
                return;

            // Check market enabled
            if (!_marketFilter.Enabled)
                return;

            // Update IndexData
            var idx = GetOrCreateIndexData(stockId, tick);
            idx.UpdateTick(tick.Price, tick.Volume);

            // Update cumulative value from tick if available
            if (tick.TodayCumulativeValue > 0)
                idx.TodayCumulativeValue = tick.TodayCumulativeValue;

            // Update limit-up locked status
            idx.IsLimitUpLocked = tick.IsLimitUpLocked;

            // Track first time reaching limit-up price
            if (idx.FirstLimitUpTime == null && idx.LimitUpPrice > 0 && tick.Price >= idx.LimitUpPrice)
                idx.FirstLimitUpTime = tick.Time;

            // Update indicator values from tick
            idx.Pct5min = tick.Pct5min;
            idx.Ratio15s300s = tick.Ratio15s300s;
            idx.BidAskRatio = tick.BidAskRatio;
            if (tick.Vwap5m > 0) idx.Vwap5m = tick.Vwap5m;

            // Update massive matching tracker (per-stock sliding window)
            if (_massiveMatchingEnabled)
            {
                idx.UpdateMassiveMatching(tick.Time, tick.TickTypeInt, tick.Price, tick.Volume, _massiveMatchingWindowMax);
            }

            // === Exit check for held positions ===
            if (_positionManager.HasPosition(stockId))
            {
                var trade = _positionManager.GetPosition(stockId)!;
                string? exitReason = _exitManager.CheckExitFull(trade, tick.Time, tick.Price,
                    tick.Low1m, tick.Low3m, tick.Low5m, tick.Low7m,
                    tick.Low10m, tick.Low15m, idx.LimitUpPrice);

                // Group rank exit: if group rank drops to/past threshold AND worse than entry rank
                if (exitReason == null && _groupRankExitEnabled && !_bypassGroupScreening)
                {
                    int currentRank = _groupScreener.GetCurrentGroupRank(trade.EntryGroupName);
                    // Exit when: rank dropped (worse than entry) AND reached threshold
                    // currentRank==0 means group is no longer valid (also exit)
                    if (currentRank == 0 ||
                        (currentRank >= _groupRankExitThreshold && currentRank > trade.EntryGroupRank))
                        exitReason = "groupRankDrop";
                }

                if (exitReason != null)
                {
                    _positionManager.ClosePosition(stockId, tick.Time, tick.Price, exitReason);
                    string trailingInfo = "";
                    if (trade.TrailingLow.Low10mExitShares > 0)
                        trailingInfo += $" tl_10m={trade.TrailingLow.Low10mExitShares:F0}@{trade.TrailingLow.Low10mExitPrice:F2}";
                    if (trade.TrailingLow.Low15mExitShares > 0)
                        trailingInfo += $" tl_15m={trade.TrailingLow.Low15mExitShares:F0}@{trade.TrailingLow.Low15mExitPrice:F2}";
                    if (trade.RollingLow.Stage1ExitShares > 0)
                        trailingInfo += $" rl1={trade.RollingLow.Stage1ExitShares:F0}@{trade.RollingLow.Stage1ExitPrice:F2}";
                    if (trade.RollingLow.Stage2ExitShares > 0)
                        trailingInfo += $" rl2={trade.RollingLow.Stage2ExitShares:F0}@{trade.RollingLow.Stage2ExitPrice:F2}";
                    if (trade.RollingLow.Stage3ExitShares > 0)
                        trailingInfo += $" rl3={trade.RollingLow.Stage3ExitShares:F0}@{trade.RollingLow.Stage3ExitPrice:F2}";
                    Console.WriteLine($"  [EXIT] {stockId} @ {tick.Price:F2} reason={exitReason} " +
                        $"time={tick.Time:HH:mm:ss} remaining={trade.RemainingShares:F0}{trailingInfo}");
                    return; // Don't evaluate entry on same tick as exit
                }
            }

            // === Strong Group screening ===
            MatchInfo? matchInfo = _groupScreener.OnTick(stockId, _allStocks);

            if (matchInfo == null)
            {
                // Stock not selected — reset Signal A near_vwap
                if (_signalAEnabled)
                    _signalA.ResetNearVwap(stockId);
                return;
            }

            // === Signal evaluation ===
            bool signalTriggered;

            if (_signalAEnabled)
            {
                // Original mode: Signal A two-phase detection
                signalTriggered = _signalA.Evaluate(stockId, tick.Time, tick.Price, idx, matchInfo);
            }
            else
            {
                // Mode E: DayHigh breakout entry when group selects stock
                // Requires price to create a new intraday high (breakout)
                var tod = tick.Time.TimeOfDay;
                bool isDayHighBreakout = idx.DayHigh > idx.PrevDayHigh && idx.PrevDayHigh > 0;
                signalTriggered = tod >= _entryStartTime
                    && tod < _entryEndTime
                    && isDayHighBreakout
                    && !_enteredStocks.Contains(stockId)
                    && !_positionManager.HasPosition(stockId);

                // Optional: require ask > bid at breakout moment
                if (signalTriggered && _requireAskGtBid)
                {
                    signalTriggered = idx.AskVolume5Level > idx.BidVolume5Level
                        && idx.AskVolume5Level > 0;
                }

                // Optional: require bid > ask (追高模式)
                if (signalTriggered && _requireBidGtAsk)
                {
                    signalTriggered = idx.BidVolume5Level > idx.AskVolume5Level
                        && idx.BidVolume5Level > 0;
                }

                // === Massive matching quality filters (Version B) ===
                if (signalTriggered && _massiveMatchingEnabled)
                {
                    signalTriggered = CheckMassiveMatchingFilters(stockId, idx, tick.Time);
                }
            }

            if (!signalTriggered)
                return;

            // === Entry ===
            var newTrade = _orderTrigger.TryEntry(
                stockId, tick.Time, idx, matchInfo,
                _positionManager.HeldStockIds);

            if (newTrade != null)
            {
                _positionManager.OpenPosition(newTrade);
                _enteredStocks.Add(stockId);
                Console.WriteLine($"  [ENTRY] {stockId} @ {newTrade.EntryPrice:F2} " +
                    $"group={matchInfo.GroupName} rank={matchInfo.GroupRank} " +
                    $"member_rank={matchInfo.MemberRank} " +
                    $"vwap={idx.Vwap:F2} dayHigh={idx.DayHigh:F2} " +
                    $"time={tick.Time:HH:mm:ss}");
            }
        }

        /// <summary>
        /// Check massive matching and quality filters for Version B entry.
        /// Returns true if all filters pass.
        /// </summary>
        private bool CheckMassiveMatchingFilters(string stockId, IndexData idx, DateTime tickTime)
        {
            // 1. Price change limit (< 8.5%)
            if (_priceChangeLimitEnabled)
            {
                double pctChg = idx.PriceChangePct * 100.0; // PriceChangePct is ratio, convert to %
                if (pctChg > _priceChangeLimitPct)
                    return false;
            }

            // 2. Massive matching amount >= threshold
            double threshold = 0;
            if (_useDynamicLiquidity && _engine != null)
            {
                threshold = _engine.GetDynamicThreshold(_currentDate, stockId);
            }
            if (threshold <= 0)
                threshold = _config.GetDouble("massive_matching_amount", 50000000.0);

            // Adaptive window: if amount > adaptive_threshold, use longer window
            double outsideAmount;
            if (_adaptiveMmWindowEnabled)
            {
                // First check with default window
                outsideAmount = idx.GetOutsideVolumeAmount(tickTime, _massiveMatchingWindow);
                if (outsideAmount < threshold)
                {
                    // Try adaptive (longer) window if amount exceeds adaptive threshold
                    double adaptiveAmount = idx.GetOutsideVolumeAmount(tickTime, _adaptiveMmWindowSeconds);
                    if (adaptiveAmount >= _adaptiveMmWindowThreshold)
                        outsideAmount = adaptiveAmount;
                }
            }
            else
            {
                outsideAmount = idx.OutsideVolumeAmount;
            }

            if (outsideAmount < threshold)
                return false;

            // 3. Interval pct filter (5min price change < 3%)
            if (_intervalPctFilterEnabled)
            {
                double pct = idx.Pct5min;
                if (!double.IsNaN(pct) && pct > _intervalPctThreshold)
                    return false;
            }

            // 4. Ratio entry (ratio_15s_300s > threshold)
            if (_ratioEntryEnabled)
            {
                double ratio = idx.Ratio15s300s;
                if (double.IsNaN(ratio) || ratio < _ratioEntryThreshold)
                    return false;
            }

            // 5. Ask/Bid ratio (bid_ask_ratio >= threshold)
            // Note: bid_ask_ratio in the data is bid/ask, so >= 1.0 means bid >= ask
            // But we want to check as ask/bid ratio threshold
            // The config ask_bid_ratio_threshold=1.0 means no filter essentially
            // Keep this as a passthrough for now since require_ask_gt_bid already handles ask > bid
            if (_askBidRatioThreshold > 0 && idx.BidAskRatio > 0)
            {
                // bid_ask_ratio from parquet is bid_volume/ask_volume
                // We want ask > bid, but this is already handled by require_ask_gt_bid
                // So this filter is for the ratio value threshold
            }

            return true;
        }

        private IndexData GetOrCreateIndexData(string stockId, RawTick tick)
        {
            if (!_allStocks.TryGetValue(stockId, out var idx))
            {
                idx = new IndexData { StockId = stockId };

                // Initialize from static data or tick
                if (_staticData.TryGetValue(stockId, out var sd))
                {
                    idx.PreviousClose = sd.PreviousClose;
                    idx.MonthlyAvgTradingValue = sd.MonthlyAvgTradingValue;
                    idx.SecurityType = sd.SecurityType;
                    idx.PrevDayLimitUp = sd.PrevDayLimitUp;
                    idx.LimitUpPrice = TickSizeHelper.CalculateLimitUp(sd.PreviousClose);
                }
                else
                {
                    idx.PreviousClose = tick.PreviousClose;
                    idx.MonthlyAvgTradingValue = tick.MonthlyAvgTradingValue;
                    idx.SecurityType = tick.SecurityType;
                    idx.PrevDayLimitUp = tick.PrevDayLimitUp;
                    if (tick.PreviousClose > 0)
                        idx.LimitUpPrice = TickSizeHelper.CalculateLimitUp(tick.PreviousClose);
                }

                _allStocks[stockId] = idx;
            }

            return idx;
        }
    }
}
