using System;

namespace StrongestVwap.Core
{
    public static class Constants
    {
        public static readonly TimeSpan MarketOpen = new(9, 0, 0);
        public static readonly TimeSpan MarketClose = new(13, 30, 0);
        public static readonly TimeSpan MarketRallyCheckTime = new(9, 15, 0);

        public const string DefaultOutputDir = @"D:\回測結果";
        public const string DefaultGroupCsvPath = @"group.csv";
        public const string TickDataBasePath = @"D:\feature_data\feature";
        public const string Symbol0050 = "0050";
        public const long PriceScale = 10000;
    }
}
