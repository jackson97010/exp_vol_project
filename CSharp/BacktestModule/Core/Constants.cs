using System;

namespace BacktestModule.Core
{
    /// <summary>
    /// Centralized constants for the backtesting system.
    /// </summary>
    public static class Constants
    {
        // ===== Path Settings =====
        public const string OutputBaseDir = @"D:\回測結果";
        public const string ScreeningResultsPath = @"C:\Users\User\Documents\_02_bt\Backtest_tick_module\screening_results.csv";
        public static readonly string TotalTradesOutput = System.IO.Path.Combine(OutputBaseDir, "backtest_trades_total.csv");
        public const string DefaultConfigPath = "configs/Bo_v2.yaml";

        // ===== Trading Parameters =====
        public const int DefaultSharesPerTrade = 1000; // 1 lot = 1000 shares
        public const double MaxGainPercentage = 8.5;
        public const string MarketOpenTimeLimitStr = "09:09:00";
        public const string MarketCloseTimeStr = "13:30:00";
        public static readonly TimeSpan MarketOpenTimeLimit = TimeSpan.Parse(MarketOpenTimeLimitStr);
        public static readonly TimeSpan MarketCloseTime = TimeSpan.Parse(MarketCloseTimeStr);

        // ===== Technical Indicator Parameters =====
        public const int DayHighMomentumWindow = 60;   // seconds
        public const int OutsideVolumeWindow = 3;       // seconds
        public const int OutsideVolumeWindow5s = 5;    // seconds (for volume shrink signal)
        public const int MassiveMatchingWindow = 1;     // seconds
        public const int IoRatioWindow = 60;             // seconds
        public const int LargeOrderThreshold = 10;       // lots

        // ===== Order Book Thresholds =====
        public const int OrderBookThinThreshold = 20;
        public const int OrderBookNormalThreshold = 40;

        // ===== Buffer Mechanism =====
        public const int BufferDurationSeconds = 3;

        // ===== Logging =====
        public const string LogFormat = "{0:HH:mm:ss} - {1} - {2}";
        public const string LogDateFormat = "HH:mm:ss";
    }

    /// <summary>
    /// Tick type enumeration: 1=Outside (buy/aggressive buy), 2=Inside (sell/aggressive sell).
    /// </summary>
    public enum TickType
    {
        Unknown = 0,
        Outside = 1,
        Inside = 2
    }

    /// <summary>
    /// Data row type: Trade or Depth.
    /// </summary>
    public enum DataRowType
    {
        Trade,
        Depth
    }

    /// <summary>
    /// Strategy mode: A=Exhaustion, B=Simple trend / trailing stop.
    /// </summary>
    public enum StrategyMode
    {
        A,
        B
    }

    /// <summary>
    /// Exit type classification.
    /// </summary>
    public enum ExitType
    {
        Partial,
        Remaining,
        TrailingStop,
        Protection,
        ReentryStop,
        MarketClose
    }
}
