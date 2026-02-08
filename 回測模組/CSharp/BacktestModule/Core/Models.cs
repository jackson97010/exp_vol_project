using System;
using System.Collections.Generic;

namespace BacktestModule.Core
{
    /// <summary>
    /// Represents a single tick/row of market data.
    /// This class is defined by Teammate 2 in BoReentryBacktest.Core.
    /// Stub provided here so Analytics/Exporters/Visualization modules can compile.
    /// </summary>
    public class TickData
    {
        public DateTime Time { get; set; }
        public double Price { get; set; }
        public double Volume { get; set; }
        public int TickType { get; set; }        // 1=outside(buy), 2=inside(sell)
        public string Type { get; set; }          // "Trade" or "Depth"
        public double DayHigh { get; set; }
        public double BidAskRatio { get; set; }
        public double Bid1Volume { get; set; }
        public double Bid2Volume { get; set; }
        public double Bid3Volume { get; set; }
        public double Bid4Volume { get; set; }
        public double Bid5Volume { get; set; }
        public double Ask1Volume { get; set; }
        public double Ask2Volume { get; set; }
        public double Ask3Volume { get; set; }
        public double Ask4Volume { get; set; }
        public double Ask5Volume { get; set; }
        public double BidVolume5Level { get; set; }
        public double AskVolume5Level { get; set; }
        public double Ratio15s300s { get; set; }
        public double Pct2Min { get; set; }
        public double Pct3Min { get; set; }
        public double Pct5Min { get; set; }
        public double Low1m { get; set; }
        public double Low3m { get; set; }
        public double Low5m { get; set; }
        public double Low10m { get; set; }
        public double Low15m { get; set; }

        // Computed metrics (filled during backtest loop by Teammate 2)
        public double DayHighGrowthRate { get; set; }
        public double BidAvgVolume { get; set; }
        public double AskAvgVolume { get; set; }
        public double BalanceRatio { get; set; }
        public bool DayHighBreakout { get; set; }
        public double InsideOutsideRatio { get; set; }
        public double OutsideRatio { get; set; }
        public double LargeOrderIoRatio { get; set; }
        public double LargeOrderOutsideRatio { get; set; }
    }

    /// <summary>
    /// Represents a completed trade record (from entry to full exit).
    /// This class is defined by Teammate 2 in BoReentryBacktest.Strategy.Position.
    /// Stub provided here so Analytics/Exporters/Visualization modules can compile.
    /// </summary>
    public class TradeRecord
    {
        public DateTime EntryTime { get; set; }
        public double EntryPrice { get; set; }
        public double EntryBidThickness { get; set; }
        public double EntryRatio { get; set; }
        public double DayHighAtEntry { get; set; }
        public double EntryOutsideVolume3s { get; set; }

        public DateTime? PartialExitTime { get; set; }
        public double? PartialExitPrice { get; set; }
        public string PartialExitReason { get; set; } = "";

        public DateTime? ReentryTime { get; set; }
        public double? ReentryPrice { get; set; }
        public double? ReentryStopPrice { get; set; }
        public double ReentryOutsideVolume3s { get; set; }

        public DateTime? FinalExitTime { get; set; }
        public double? FinalExitPrice { get; set; }
        public string FinalExitReason { get; set; } = "";

        public string ReentryExitReason { get; set; }

        public double? PnlPercent { get; set; }

        /// <summary>
        /// List of trailing stop exit details. Each dictionary contains:
        /// "time" (DateTime), "price" (double), "ratio" (double), "level" (string), "reason" (string).
        /// </summary>
        public List<Dictionary<string, object>> TrailingExitDetails { get; set; } = new();

        public int TotalExits { get; set; }
        public double FinalRemainingRatio { get; set; } = 1.0;

        /// <summary>
        /// Converts the trade record to a dictionary representation.
        /// </summary>
        public Dictionary<string, object> ToDict()
        {
            var dict = new Dictionary<string, object>
            {
                ["entry_time"] = EntryTime,
                ["entry_price"] = EntryPrice,
                ["entry_bid_thickness"] = EntryBidThickness,
                ["entry_ratio"] = EntryRatio,
                ["day_high_at_entry"] = DayHighAtEntry,
                ["entry_outside_volume_3s"] = EntryOutsideVolume3s,
                ["partial_exit_time"] = PartialExitTime,
                ["partial_exit_price"] = PartialExitPrice,
                ["partial_exit_reason"] = PartialExitReason,
                ["reentry_time"] = ReentryTime,
                ["reentry_price"] = ReentryPrice,
                ["reentry_stop_price"] = ReentryStopPrice,
                ["reentry_outside_volume_3s"] = ReentryOutsideVolume3s,
                ["final_exit_time"] = FinalExitTime,
                ["final_exit_price"] = FinalExitPrice,
                ["final_exit_reason"] = FinalExitReason,
                ["reentry_exit_reason"] = ReentryExitReason,
                ["pnl_percent"] = PnlPercent,
                ["trailing_exit_details"] = TrailingExitDetails,
                ["total_exits"] = TotalExits,
                ["final_remaining_ratio"] = FinalRemainingRatio
            };
            return dict;
        }
    }

    /// <summary>
    /// Represents an entry signal result.
    /// This class is defined by Teammate 2 in BoReentryBacktest.Strategy.Entry.
    /// Stub provided here so ReportGenerator can compile.
    /// </summary>
    public class EntrySignal
    {
        public DateTime Time { get; set; }
        public string StockId { get; set; }
        public double Price { get; set; }
        public double DayHigh { get; set; }
        public bool Passed { get; set; }
        public Dictionary<string, Dictionary<string, object>> Conditions { get; set; } = new();
        public string FailureReason { get; set; }
    }
}
