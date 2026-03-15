using System;
using StrongestVwap.Core;

namespace StrongestVwap.Strategy
{
    public class MarketFilter
    {
        private readonly double _openMinChg;
        private readonly double _rallyDisableThreshold;

        public double PrevClose0050 { get; set; }
        public double CurrentPrice0050 { get; set; }
        public bool Enabled { get; private set; } = true;
        public bool OpenCheckDone { get; private set; }
        public bool RallyCheckDone { get; private set; }
        public string DisableReason { get; private set; } = "";

        public MarketFilter(StrategyConfig config)
        {
            _openMinChg = config.GetDouble("market_open_min_chg", 0);
            _rallyDisableThreshold = config.GetDouble("market_rally_disable_threshold", 0.2);
        }

        public bool UpdateTick(DateTime time, double price0050)
        {
            CurrentPrice0050 = price0050;

            if (!OpenCheckDone && time.TimeOfDay >= Constants.MarketOpen)
            {
                OpenCheckDone = true;
                if (PrevClose0050 > 0)
                {
                    double openChg = (price0050 - PrevClose0050) / PrevClose0050;
                    if (openChg < _openMinChg)
                    {
                        Enabled = false;
                        DisableReason = $"0050 open chg {openChg:P2} < {_openMinChg:P2}";
                        Console.WriteLine($"[MARKET FILTER] {DisableReason}");
                    }
                }
            }

            if (!RallyCheckDone && time.TimeOfDay >= Constants.MarketRallyCheckTime)
            {
                RallyCheckDone = true;
                if (PrevClose0050 > 0)
                {
                    double chg = (price0050 - PrevClose0050) / PrevClose0050;
                    if (chg >= _rallyDisableThreshold)
                    {
                        Enabled = false;
                        DisableReason = $"0050 rally {chg:P2} >= {_rallyDisableThreshold:P2}";
                        Console.WriteLine($"[MARKET FILTER] {DisableReason}");
                    }
                }
            }

            return Enabled;
        }

        public static bool ShouldSkipTick(int tradeCode, string stockId)
        {
            if (tradeCode != 1) return true;
            if (stockId.StartsWith("00")) return true;
            return false;
        }
    }
}
