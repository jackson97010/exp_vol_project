using System;

namespace BacktestModule.Core.Models
{
    /// <summary>
    /// Represents a single row of tick data from the parquet feature file.
    /// Replaces the pandas DataFrame row.
    /// </summary>
    public class TickData
    {
        public DateTime Time { get; set; }
        public double Price { get; set; }
        public double Volume { get; set; }
        public int TickType { get; set; }       // 1=outside(buy), 2=inside(sell), 0=unknown
        public string Type { get; set; } = "";  // "Trade" or "Depth"
        public double DayHigh { get; set; }
        public double BidAskRatio { get; set; }

        // Order book 5-level volumes
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

        // Aggregated order book volumes
        public double BidVolume5Level { get; set; }
        public double AskVolume5Level { get; set; }

        // Ratio and percentage change indicators
        public double Ratio15s300s { get; set; }
        public double Ratio15s180sW321 { get; set; }
        public double Pct2Min { get; set; }
        public double Pct3Min { get; set; }
        public double Pct5Min { get; set; }

        // Rolling low prices
        public double Low1m { get; set; }
        public double Low3m { get; set; }
        public double Low5m { get; set; }
        public double Low7m { get; set; }
        public double Low10m { get; set; }
        public double Low15m { get; set; }

        // Rolling high prices
        public double High3m { get; set; }

        // VWAP
        public double Vwap { get; set; }
        public double Vwap5m { get; set; }

        // Computed metrics (filled during backtest loop)
        public double DayHighGrowthRate { get; set; }
        public double BidAvgVolume { get; set; }
        public double AskAvgVolume { get; set; }
        public double BalanceRatio { get; set; }
        public bool DayHighBreakout { get; set; }
        public double InsideOutsideRatio { get; set; }
        public double OutsideRatio { get; set; }
        public double LargeOrderIoRatio { get; set; }
        public double LargeOrderOutsideRatio { get; set; }

        // Observation signals (mark only, do not trigger exit)
        public bool VolumeShrinkSignal { get; set; }
        public bool VwapDeviationSignal { get; set; }

        /// <summary>
        /// Gets a field value by name, used for dynamic access (e.g., trailing stop field lookup).
        /// </summary>
        public double GetFieldByName(string fieldName)
        {
            return fieldName switch
            {
                "low_1m" => Low1m,
                "low_3m" => Low3m,
                "low_5m" => Low5m,
                "low_7m" => Low7m,
                "low_10m" => Low10m,
                "low_15m" => Low15m,
                "bid_volume_5level" => BidVolume5Level,
                "ask_volume_5level" => AskVolume5Level,
                "bid_ask_ratio" => BidAskRatio,
                "ratio_15s_300s" => Ratio15s300s,
                "ratio_15s_180s_w321" => Ratio15s180sW321,
                "pct_2min" => Pct2Min,
                "pct_3min" => Pct3Min,
                "pct_5min" => Pct5Min,
                "high_3m" => High3m,
                "vwap" => Vwap,
                "vwap_5m" => Vwap5m,
                _ => 0.0
            };
        }
    }
}
