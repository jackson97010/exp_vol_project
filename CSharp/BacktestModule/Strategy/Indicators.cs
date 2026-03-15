using System;
using System.Collections.Generic;
using System.Linq;
using BacktestModule.Core.Models;

namespace BacktestModule.Strategy
{
    /// <summary>
    /// Day High Momentum Tracker: tracks Day High growth rate over a sliding window.
    /// </summary>
    public class DayHighMomentumTracker
    {
        private readonly int _windowSeconds;
        private readonly LinkedList<(DateTime Time, double DayHigh)> _dayHighHistory = new();
        private readonly LinkedList<double> _growthRateHistory = new();
        private const int MaxGrowthRateHistoryLength = 20;

        public double CurrentDayHigh { get; private set; }
        public double DayHigh1MinAgo { get; private set; }
        public double LastGrowthRate { get; private set; }
        public double PeakGrowthRate { get; private set; }

        public DayHighMomentumTracker(int windowSeconds = 60)
        {
            _windowSeconds = windowSeconds;
        }

        public void Update(DateTime currentTime, double dayHigh)
        {
            _dayHighHistory.AddLast((currentTime, dayHigh));
            CurrentDayHigh = dayHigh;

            // Remove expired records
            var cutoff = currentTime.AddSeconds(-_windowSeconds);
            while (_dayHighHistory.Count > 0 && _dayHighHistory.First.Value.Time < cutoff)
                _dayHighHistory.RemoveFirst();

            // Day high from 1 minute ago is the oldest in the window
            DayHigh1MinAgo = _dayHighHistory.Count > 0 ? _dayHighHistory.First.Value.DayHigh : dayHigh;

            // Compute and store growth rate (once per tick)
            ComputeAndRecordGrowthRate();
        }

        /// <summary>
        /// Returns the last computed growth rate without side effects.
        /// Safe to call multiple times per tick.
        /// </summary>
        public double GetGrowthRate()
        {
            return LastGrowthRate;
        }

        /// <summary>
        /// Computes and records growth rate. Called once per tick from Update().
        /// </summary>
        private void ComputeAndRecordGrowthRate()
        {
            if (DayHigh1MinAgo > 0 && CurrentDayHigh != DayHigh1MinAgo)
                LastGrowthRate = (CurrentDayHigh - DayHigh1MinAgo) / DayHigh1MinAgo;
            else
                LastGrowthRate = 0.0;

            _growthRateHistory.AddLast(LastGrowthRate);
            while (_growthRateHistory.Count > MaxGrowthRateHistoryLength)
                _growthRateHistory.RemoveFirst();

            PeakGrowthRate = Math.Max(PeakGrowthRate, LastGrowthRate);
        }

        public bool IsGrowthRateTurningDown()
        {
            if (_growthRateHistory.Count < 2) return false;
            var node = _growthRateHistory.Last;
            var prevNode = node.Previous;
            return node.Value < prevNode.Value;
        }

        public double GetGrowthDrawdown()
        {
            return Math.Max(PeakGrowthRate - LastGrowthRate, 0.0);
        }

        public void Reset()
        {
            _dayHighHistory.Clear();
            _growthRateHistory.Clear();
            CurrentDayHigh = 0;
            DayHigh1MinAgo = 0;
            LastGrowthRate = 0;
            PeakGrowthRate = 0;
        }
    }

    /// <summary>
    /// Order Book Balance Monitor: analyzes bid/ask thickness and balance.
    /// </summary>
    public class OrderBookBalanceMonitor
    {
        private readonly int _thinThreshold;
        private readonly int _normalThreshold;

        public OrderBookBalanceMonitor(int thinThreshold = 20, int normalThreshold = 40)
        {
            _thinThreshold = thinThreshold;
            _normalThreshold = normalThreshold;
        }

        /// <summary>
        /// Calculates bid thickness: sum of bid1-bid5 volumes / 5.
        /// Falls back to bid_volume_5level / 5.
        /// </summary>
        public double CalculateBidThickness(TickData row)
        {
            double bidTotal = row.BidVolume5Level;

            if (double.IsNaN(bidTotal) || bidTotal == 0)
            {
                double sum = 0;
                if (!double.IsNaN(row.Bid1Volume)) sum += row.Bid1Volume;
                if (!double.IsNaN(row.Bid2Volume)) sum += row.Bid2Volume;
                if (!double.IsNaN(row.Bid3Volume)) sum += row.Bid3Volume;
                if (!double.IsNaN(row.Bid4Volume)) sum += row.Bid4Volume;
                if (!double.IsNaN(row.Bid5Volume)) sum += row.Bid5Volume;
                if (sum > 0) bidTotal = sum;
            }

            return bidTotal > 0 ? bidTotal / 5.0 : 0.0;
        }

        /// <summary>
        /// Calculates ask thickness: sum of ask1-ask5 volumes / 5.
        /// </summary>
        public double CalculateAskThickness(TickData row)
        {
            double askTotal = row.AskVolume5Level;

            if (double.IsNaN(askTotal) || askTotal == 0)
            {
                double sum = 0;
                if (!double.IsNaN(row.Ask1Volume)) sum += row.Ask1Volume;
                if (!double.IsNaN(row.Ask2Volume)) sum += row.Ask2Volume;
                if (!double.IsNaN(row.Ask3Volume)) sum += row.Ask3Volume;
                if (!double.IsNaN(row.Ask4Volume)) sum += row.Ask4Volume;
                if (!double.IsNaN(row.Ask5Volume)) sum += row.Ask5Volume;
                if (sum > 0) askTotal = sum;
            }

            return askTotal > 0 ? askTotal / 5.0 : 0.0;
        }

        /// <summary>
        /// Calculates buy/sell balance ratio: total_bid / total_ask.
        /// </summary>
        public double CalculateBalanceRatio(TickData row)
        {
            double totalBid = row.BidVolume5Level;
            if (double.IsNaN(totalBid) || totalBid == 0)
            {
                double sum = 0;
                if (!double.IsNaN(row.Bid1Volume)) sum += row.Bid1Volume;
                if (!double.IsNaN(row.Bid2Volume)) sum += row.Bid2Volume;
                if (!double.IsNaN(row.Bid3Volume)) sum += row.Bid3Volume;
                if (!double.IsNaN(row.Bid4Volume)) sum += row.Bid4Volume;
                if (!double.IsNaN(row.Bid5Volume)) sum += row.Bid5Volume;
                totalBid = sum;
            }

            double totalAsk = row.AskVolume5Level;
            if (double.IsNaN(totalAsk) || totalAsk == 0)
            {
                double sum = 0;
                if (!double.IsNaN(row.Ask1Volume)) sum += row.Ask1Volume;
                if (!double.IsNaN(row.Ask2Volume)) sum += row.Ask2Volume;
                if (!double.IsNaN(row.Ask3Volume)) sum += row.Ask3Volume;
                if (!double.IsNaN(row.Ask4Volume)) sum += row.Ask4Volume;
                if (!double.IsNaN(row.Ask5Volume)) sum += row.Ask5Volume;
                totalAsk = sum;
            }

            if (totalBid > 0 && totalAsk > 0)
                return totalBid / totalAsk;
            return 0.0;
        }

        /// <summary>
        /// Checks if order book is recovering from thin to normal.
        /// </summary>
        public bool IsOrderBookRecovering(TickData currentRow, double entryBidThickness)
        {
            double currentBidThickness = CalculateBidThickness(currentRow);
            return entryBidThickness < _thinThreshold && currentBidThickness > _normalThreshold;
        }
    }

    /// <summary>
    /// Outside Volume Tracker: tracks outside (buy) volume amount in a sliding time window.
    /// Amount = price * volume_lots * 1000 (1 lot = 1000 shares).
    /// </summary>
    public class OutsideVolumeTracker
    {
        private readonly int _windowSeconds;
        private readonly LinkedList<(DateTime Time, double Amount)> _tradesWindow = new();
        private double _totalVolume;

        public OutsideVolumeTracker(int windowSeconds = 3)
        {
            _windowSeconds = windowSeconds;
        }

        /// <summary>
        /// Updates with a new tick. Removes expired trades. Adds outside ticks.
        /// </summary>
        public void UpdateTrades(DateTime currentTime, int tickType, double price, double volume)
        {
            // Clean expired
            var cutoff = currentTime.AddSeconds(-_windowSeconds);
            while (_tradesWindow.Count > 0 && _tradesWindow.First.Value.Time < cutoff)
            {
                var expired = _tradesWindow.First.Value;
                _tradesWindow.RemoveFirst();
                _totalVolume -= expired.Amount;
            }

            // Only record outside trades (tickType == 1)
            if (tickType == 1)
            {
                double tradeAmount = price * volume * 1000; // lots -> shares -> amount
                _tradesWindow.AddLast((currentTime, tradeAmount));
                _totalVolume += tradeAmount;
            }
        }

        public double GetVolume3s() => _totalVolume;

        public double GetCurrentVolume() => _totalVolume;

        public (double CurrentVolume, bool IsGreater) CompareWithEntry(double entryVolume)
        {
            return (_totalVolume, _totalVolume > entryVolume);
        }

        public void Reset()
        {
            _tradesWindow.Clear();
            _totalVolume = 0.0;
        }

        public int GetTradeCount() => _tradesWindow.Count;

        public double GetAverageTradeAmount()
        {
            return _tradesWindow.Count > 0 ? _totalVolume / _tradesWindow.Count : 0.0;
        }
    }

    /// <summary>
    /// Massive Matching Tracker: wraps OutsideVolumeTracker with 1-second window.
    /// </summary>
    public class MassiveMatchingTracker
    {
        private readonly OutsideVolumeTracker _outsideTracker;

        public MassiveMatchingTracker(int windowSeconds = 1)
        {
            _outsideTracker = new OutsideVolumeTracker(windowSeconds);
        }

        public void Update(DateTime currentTime, int tickType, double price, double volume)
        {
            _outsideTracker.UpdateTrades(currentTime, tickType, price, volume);
        }

        public double GetMassiveMatchingAmount() => _outsideTracker.GetCurrentVolume();

        public bool CheckThreshold(double threshold)
        {
            return GetMassiveMatchingAmount() >= threshold;
        }

        public void Reset()
        {
            _outsideTracker.Reset();
        }
    }

    /// <summary>
    /// Inside/Outside Ratio Tracker: tracks inside/outside volume ratio over a sliding window.
    /// </summary>
    public class InsideOutsideRatioTracker
    {
        private readonly int _windowSeconds;
        private readonly double _minVolumeThreshold;
        private readonly LinkedList<(DateTime Time, double Volume)> _insideTrades = new();
        private readonly LinkedList<(DateTime Time, double Volume)> _outsideTrades = new();
        private double _insideVolume;
        private double _outsideVolume;

        public InsideOutsideRatioTracker(int windowSeconds = 60, double minVolumeThreshold = 0)
        {
            _windowSeconds = windowSeconds;
            _minVolumeThreshold = minVolumeThreshold;
        }

        public void Update(DateTime currentTime, int tickType, double price, double volume)
        {
            // Check minimum volume threshold
            if (volume <= _minVolumeThreshold)
            {
                CleanupExpired(currentTime);
                return;
            }

            CleanupExpired(currentTime);

            double tradeVolume = volume; // Keep in lots

            if (tickType == 1) // Outside
            {
                _outsideTrades.AddLast((currentTime, tradeVolume));
                _outsideVolume += tradeVolume;
            }
            else if (tickType == 2) // Inside
            {
                _insideTrades.AddLast((currentTime, tradeVolume));
                _insideVolume += tradeVolume;
            }
        }

        private void CleanupExpired(DateTime currentTime)
        {
            var cutoff = currentTime.AddSeconds(-_windowSeconds);

            while (_insideTrades.Count > 0 && _insideTrades.First.Value.Time < cutoff)
            {
                var expired = _insideTrades.First.Value;
                _insideTrades.RemoveFirst();
                _insideVolume -= expired.Volume;
            }

            while (_outsideTrades.Count > 0 && _outsideTrades.First.Value.Time < cutoff)
            {
                var expired = _outsideTrades.First.Value;
                _outsideTrades.RemoveFirst();
                _outsideVolume -= expired.Volume;
            }
        }

        /// <summary>
        /// inside_volume / outside_volume (0 if outside is 0).
        /// </summary>
        public double GetRatio()
        {
            return _outsideVolume > 0 ? _insideVolume / _outsideVolume : 0.0;
        }

        /// <summary>
        /// outside_volume / (inside + outside) (0.5 if total is 0).
        /// </summary>
        public double GetOutsideRatio()
        {
            double total = _insideVolume + _outsideVolume;
            return total > 0 ? _outsideVolume / total : 0.5;
        }

        public (double InsideVolume, double OutsideVolume) GetVolumes()
        {
            return (_insideVolume, _outsideVolume);
        }

        public void Reset()
        {
            _insideTrades.Clear();
            _outsideTrades.Clear();
            _insideVolume = 0.0;
            _outsideVolume = 0.0;
        }
    }
}
