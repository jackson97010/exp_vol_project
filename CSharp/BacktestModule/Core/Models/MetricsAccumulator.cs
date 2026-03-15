using System.Collections.Generic;

namespace BacktestModule.Core.Models
{
    /// <summary>
    /// Accumulates metric lists during the backtest loop, later attached to tick data.
    /// </summary>
    public class MetricsAccumulator
    {
        public List<double> DayHighGrowthRates { get; set; }
        public List<double> BidAvgVolumes { get; set; }
        public List<double> AskAvgVolumes { get; set; }
        public List<double> BalanceRatios { get; set; }
        public List<bool> DayHighBreakouts { get; set; }
        public List<double> InsideOutsideRatios { get; set; }
        public List<double> OutsideRatios { get; set; }
        public List<double> LargeOrderIoRatios { get; set; }
        public List<double> LargeOrderOutsideRatios { get; set; }

        // Observation signals
        public List<bool> VolumeShrinkSignals { get; set; }
        public List<bool> VwapDeviationSignals { get; set; }

        public MetricsAccumulator(int capacity = 0)
        {
            DayHighGrowthRates = new List<double>(capacity);
            BidAvgVolumes = new List<double>(capacity);
            AskAvgVolumes = new List<double>(capacity);
            BalanceRatios = new List<double>(capacity);
            DayHighBreakouts = new List<bool>(capacity);
            InsideOutsideRatios = new List<double>(capacity);
            OutsideRatios = new List<double>(capacity);
            LargeOrderIoRatios = new List<double>(capacity);
            LargeOrderOutsideRatios = new List<double>(capacity);
            VolumeShrinkSignals = new List<bool>(capacity);
            VwapDeviationSignals = new List<bool>(capacity);
        }
    }
}
