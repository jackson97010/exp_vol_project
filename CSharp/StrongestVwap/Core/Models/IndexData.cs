using System;
using System.Collections.Generic;

namespace StrongestVwap.Core.Models
{
    public class IndexData
    {
        public string StockId { get; set; } = "";
        public double PreviousClose { get; set; }
        public double LastPrice { get; set; }
        public double Vwap { get; set; }
        public double DayHigh { get; set; }
        public double DayLow { get; set; } = double.MaxValue;
        public double CumulativeVolume { get; set; }
        public double CumulativePriceVolume { get; set; }
        public double MonthlyAvgTradingValue { get; set; }
        public double TodayCumulativeValue { get; set; }
        public double LimitUpPrice { get; set; }
        public bool IsLimitUpLocked { get; set; }
        public bool PrevDayLimitUp { get; set; }
        public string SecurityType { get; set; } = "";

        // Order book 5-level aggregates (latest from Depth ticks)
        public double BidVolume5Level { get; set; }
        public double AskVolume5Level { get; set; }

        // Latest indicator values (updated from tick data)
        public double Pct5min { get; set; }
        public double Ratio15s300s { get; set; }
        public double BidAskRatio { get; set; }
        public double Vwap5m { get; set; }

        // Massive matching: sliding window of outside (buy) trade amounts
        public LinkedList<(DateTime Time, double Amount)> OutsideTradesWindow { get; } = new();
        public double OutsideVolumeAmount { get; set; }

        public double PriceChangePct => PreviousClose > 0 ? (LastPrice - PreviousClose) / PreviousClose : 0;
        public double VwapChangePct => PreviousClose > 0 ? (Vwap - PreviousClose) / PreviousClose : 0;
        public double Vwap5mChangePct => PreviousClose > 0 && Vwap5m > 0 ? (Vwap5m - PreviousClose) / PreviousClose : 0;

        public double PrevDayHigh { get; set; }

        // First time price reached limit-up (for tiebreaker ranking)
        public DateTime? FirstLimitUpTime { get; set; }

        public void UpdateTick(double price, double volume)
        {
            LastPrice = price;
            CumulativeVolume += volume;
            CumulativePriceVolume += price * volume;
            TodayCumulativeValue += price * volume;

            if (CumulativeVolume > 0)
                Vwap = CumulativePriceVolume / CumulativeVolume;

            // Track previous DayHigh before updating
            PrevDayHigh = DayHigh;

            if (price > DayHigh)
                DayHigh = price;

            if (price < DayLow)
                DayLow = price;
        }

        /// <summary>
        /// Update massive matching sliding window with a new trade tick.
        /// Only outside trades (tickType == 1) are accumulated.
        /// </summary>
        public void UpdateMassiveMatching(DateTime time, int tickType, double price, double volume, int windowSeconds)
        {
            // Clean expired
            var cutoff = time.AddSeconds(-windowSeconds);
            while (OutsideTradesWindow.Count > 0 && OutsideTradesWindow.First!.Value.Time < cutoff)
            {
                var expired = OutsideTradesWindow.First.Value;
                OutsideTradesWindow.RemoveFirst();
                OutsideVolumeAmount -= expired.Amount;
            }

            // Only record outside trades (tickType == 1)
            if (tickType == 1)
            {
                double tradeAmount = price * volume * 1000; // lots -> shares -> amount
                OutsideTradesWindow.AddLast((time, tradeAmount));
                OutsideVolumeAmount += tradeAmount;
            }
        }

        /// <summary>
        /// Get outside volume amount for a sub-window (e.g. 1s within a 2s window).
        /// </summary>
        public double GetOutsideVolumeAmount(DateTime currentTime, int windowSeconds)
        {
            var cutoff = currentTime.AddSeconds(-windowSeconds);
            double sum = 0;
            var node = OutsideTradesWindow.Last;
            while (node != null && node.Value.Time >= cutoff)
            {
                sum += node.Value.Amount;
                node = node.Previous;
            }
            return sum;
        }
    }
}
