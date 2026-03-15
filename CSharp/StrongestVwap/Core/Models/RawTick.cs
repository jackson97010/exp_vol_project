using System;

namespace StrongestVwap.Core.Models
{
    public class RawTick
    {
        public DateTime Time { get; set; }
        public string StockId { get; set; } = "";
        public double Price { get; set; }
        public double Volume { get; set; }
        public int TradeCode { get; set; }
        public string TickType { get; set; } = "";
        public double AskPrice0 { get; set; }
        public string SecurityType { get; set; } = "";
        public double PreviousClose { get; set; }
        public double MonthlyAvgTradingValue { get; set; }
        public double TodayCumulativeValue { get; set; }
        public bool IsLimitUpLocked { get; set; }
        public bool PrevDayLimitUp { get; set; }

        // Trailing low fields
        public double Low1m { get; set; }
        public double Low3m { get; set; }
        public double Low5m { get; set; }
        public double Low7m { get; set; }
        public double Low10m { get; set; }
        public double Low15m { get; set; }

        // Row type: "Trade" or "Depth"
        public string RowType { get; set; } = "Trade";

        // Order book 5-level aggregates (from Depth rows)
        public double BidVolume5Level { get; set; }
        public double AskVolume5Level { get; set; }

        // Massive matching / quality filter fields
        public int TickTypeInt { get; set; }       // 0=open, 1=outside/buy, 2=inside/sell
        public double Pct5min { get; set; }        // 5-minute price change %
        public double Ratio15s300s { get; set; }   // ratio_15s_300s indicator
        public double BidAskRatio { get; set; }    // bid_ask_ratio (bid/ask volume ratio)

        // 5-minute rolling VWAP
        public double Vwap5m { get; set; }
    }
}
