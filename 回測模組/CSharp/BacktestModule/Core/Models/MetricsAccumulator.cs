using System.Collections.Generic;

namespace BacktestModule.Core.Models
{
    /// <summary>
    /// Accumulates metric lists during the backtest loop, later attached to tick data.
    /// </summary>
    public class MetricsAccumulator
    {
        public List<double> DayHighGrowthRates { get; set; } = new List<double>();
        public List<double> BidAvgVolumes { get; set; } = new List<double>();
        public List<double> AskAvgVolumes { get; set; } = new List<double>();
        public List<double> BalanceRatios { get; set; } = new List<double>();
        public List<bool> DayHighBreakouts { get; set; } = new List<bool>();
        public List<double> InsideOutsideRatios { get; set; } = new List<double>();
        public List<double> OutsideRatios { get; set; } = new List<double>();
        public List<double> LargeOrderIoRatios { get; set; } = new List<double>();
        public List<double> LargeOrderOutsideRatios { get; set; } = new List<double>();
    }
}
