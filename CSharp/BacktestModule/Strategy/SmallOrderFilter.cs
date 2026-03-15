using System;
using System.Collections.Generic;
using System.Linq;
using BacktestModule.Core.Models;

namespace BacktestModule.Strategy
{
    /// <summary>
    /// Small Order Filter and Breakout Quality Checker.
    /// Filters out "small order push" fake breakouts and checks breakout quality.
    /// </summary>
    public class SmallOrderFilter
    {
        // Small order filter (old strategy)
        public bool Enabled { get; }
        // Breakout quality check (new strategy)
        public bool BreakoutQualityEnabled { get; }

        private readonly int _breakoutMinVolume;
        private readonly double _breakoutMinAskEatRatio;
        private readonly int _breakoutAbsoluteLargeVolume;
        private readonly int _checkTrades;
        private readonly int _smallThreshold;
        private readonly int _tinyThreshold;
        private readonly int _singleThreshold;
        private readonly double _smallRatioLimit;
        private readonly double _tinyRatioLimit;
        private readonly double _singleRatioLimit;
        private readonly bool _requireLargeOrder;
        private readonly int _largeThreshold;
        private readonly int _minLargeOrders;

        // Pre-cached trade data (filtered once, reused across calls)
        private List<TickData> _cachedTradeData;

        public SmallOrderFilter(Dictionary<string, object> config)
        {
            Enabled = GetBool(config, "small_order_filter_enabled", false);
            BreakoutQualityEnabled = GetBool(config, "breakout_quality_check_enabled", true);
            _breakoutMinVolume = GetInt(config, "breakout_min_volume", 10);
            _breakoutMinAskEatRatio = GetDouble(config, "breakout_min_ask_eat_ratio", 0.25);
            _breakoutAbsoluteLargeVolume = GetInt(config, "breakout_absolute_large_volume", 50);
            _checkTrades = GetInt(config, "small_order_check_trades", 30);
            _smallThreshold = GetInt(config, "small_order_threshold", 3);
            _tinyThreshold = GetInt(config, "tiny_order_threshold", 2);
            _singleThreshold = GetInt(config, "single_order_threshold", 1);
            _smallRatioLimit = GetDouble(config, "small_order_ratio_limit", 0.8);
            _tinyRatioLimit = GetDouble(config, "tiny_order_ratio_limit", 0.5);
            _singleRatioLimit = GetDouble(config, "single_order_ratio_limit", 0.4);
            _requireLargeOrder = GetBool(config, "require_large_order_confirmation", false);
            _largeThreshold = GetInt(config, "large_order_threshold", 20);
            _minLargeOrders = GetInt(config, "min_large_orders", 2);
        }

        /// <summary>
        /// Pre-filters trade data once for the entire backtest run.
        /// Call at the start of BacktestLoop.Run() to avoid repeated .Where().ToList() per tick.
        /// </summary>
        public void PreFilterTradeData(List<TickData> data)
        {
            _cachedTradeData = data.Where(d => d.Type == "Trade").ToList();
        }

        /// <summary>
        /// Binary search to find the index of the last element with Time &lt; targetTime.
        /// Returns -1 if no such element exists.
        /// </summary>
        private static int BinarySearchLastBefore(List<TickData> sortedData, DateTime targetTime)
        {
            int lo = 0, hi = sortedData.Count - 1, result = -1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (sortedData[mid].Time < targetTime)
                {
                    result = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return result;
        }

        /// <summary>
        /// Checks if recent trades exhibit a small order push pattern.
        /// Returns (passedCheck, reason).
        /// </summary>
        public (bool Passed, string Reason) CheckSmallOrderPattern(
            List<TickData> data, DateTime currentTime)
        {
            if (!Enabled)
                return (true, "Small order filter disabled");

            var tradeData = _cachedTradeData ?? data.Where(d => d.Type == "Trade").ToList();
            if (tradeData.Count == 0)
                return (true, "No trade data");

            // Get last N trades
            var recentTrades = tradeData.TakeLast(_checkTrades).ToList();
            if (recentTrades.Count < 10)
                return (true, $"Insufficient trades ({recentTrades.Count})");

            int total = recentTrades.Count;
            int singleCount = recentTrades.Count(t => t.Volume == _singleThreshold);
            int tinyCount = recentTrades.Count(t => t.Volume <= _tinyThreshold);
            int smallCount = recentTrades.Count(t => t.Volume <= _smallThreshold);

            double singleRatio = (double)singleCount / total;
            double tinyRatio = (double)tinyCount / total;
            double smallRatio = (double)smallCount / total;

            if (singleRatio > _singleRatioLimit)
                return (false, $"Single order ratio too high: {singleRatio:P1}");

            if (tinyRatio > _tinyRatioLimit)
                return (false, $"Tiny order (<=({_tinyThreshold}) lots) ratio too high: {tinyRatio:P1}");

            if (smallRatio > _smallRatioLimit)
                return (false, $"Small order (<={_smallThreshold} lots) ratio too high: {smallRatio:P1}");

            if (_requireLargeOrder)
            {
                int largeCount = recentTrades.Count(t => t.Volume >= _largeThreshold);
                if (largeCount < _minLargeOrders)
                    return (false, $"Lacking large order confirmation (only {largeCount} >= {_largeThreshold} lots)");
            }

            return (true, "Passed small order check");
        }

        /// <summary>
        /// Checks breakout quality: whether the breakout tick has sufficient volume.
        /// Returns (passedCheck, reason).
        /// </summary>
        public (bool Passed, string Reason) CheckBreakoutQuality(
            TickData currentRow,
            bool isDayHighBreakout,
            List<TickData> data = null)
        {
            if (!BreakoutQualityEnabled)
                return (true, "Breakout quality check disabled");

            if (!isDayHighBreakout)
                return (true, "Not a Day High breakout");

            double currentVolume = currentRow.Volume;

            if (data == null)
            {
                // Old logic: check breakout tick only
                if (currentVolume >= _breakoutAbsoluteLargeVolume)
                    return (true, $"Absolute large order breakout ({currentVolume:F0} lots)");
                if (currentVolume >= _breakoutMinVolume)
                    return (true, $"Sufficient breakout volume ({currentVolume:F0} lots)");
                return (false, $"Breakout volume too small: {currentVolume:F0} lots");
            }

            // New logic: check if breakout tick ate a significant portion of ask 5-level
            DateTime currentTime = currentRow.Time;

            // Get ask 5-level total from previous tick (using binary search for O(log n))
            double askTotal = 0;
            var tradeData = _cachedTradeData ?? data.Where(d => d.Type == "Trade").ToList();
            int lastBeforeIdx = BinarySearchLastBefore(tradeData, currentTime);
            if (lastBeforeIdx >= 0)
            {
                var prevRow = tradeData[lastBeforeIdx];
                askTotal = prevRow.AskVolume5Level;
                if (double.IsNaN(askTotal) || askTotal == 0)
                {
                    askTotal = SumAskVolumes(prevRow);
                }
            }

            // Fallback to current row's ask volumes
            if (askTotal == 0)
            {
                askTotal = currentRow.AskVolume5Level;
                if (double.IsNaN(askTotal) || askTotal == 0)
                    askTotal = SumAskVolumes(currentRow);
            }

            // Condition 1: Absolute large order (>= 50 lots)
            if (currentVolume >= _breakoutAbsoluteLargeVolume)
                return (true, $"Absolute large order breakout ({currentVolume:F0} lots)");

            // Condition 2: Large order (>= 10 lots) AND ate >= 25% of ask 5-level
            if (currentVolume >= _breakoutMinVolume)
            {
                if (askTotal > 0)
                {
                    double eatRatio = currentVolume / askTotal;
                    if (eatRatio >= _breakoutMinAskEatRatio)
                        return (true, $"Large order ate {eatRatio:P0} of ask ({currentVolume:F0}/{askTotal:F0} lots)");
                    else
                        return (false, $"Ask eat ratio insufficient: {eatRatio:P0}");
                }
                else
                {
                    return (true, $"Large order breakout ({currentVolume:F0} lots)");
                }
            }

            return (false, $"Breakout volume too small: {currentVolume:F0} lots");
        }

        /// <summary>
        /// Gets trade statistics for recent trades.
        /// </summary>
        public Dictionary<string, object> GetTradeStatistics(List<TickData> data, DateTime currentTime)
        {
            var tradeData = _cachedTradeData ?? data.Where(d => d.Type == "Trade").ToList();
            if (tradeData.Count == 0)
                return new Dictionary<string, object>();

            var recentTrades = tradeData.TakeLast(_checkTrades).ToList();
            if (recentTrades.Count == 0)
                return new Dictionary<string, object>();

            int total = recentTrades.Count;
            return new Dictionary<string, object>
            {
                ["total_trades"] = total,
                ["total_volume"] = recentTrades.Sum(t => t.Volume),
                ["avg_volume"] = recentTrades.Average(t => t.Volume),
                ["single_count"] = recentTrades.Count(t => t.Volume == 1),
                ["tiny_count"] = recentTrades.Count(t => t.Volume <= _tinyThreshold),
                ["small_count"] = recentTrades.Count(t => t.Volume <= _smallThreshold),
                ["large_count"] = recentTrades.Count(t => t.Volume >= _largeThreshold)
            };
        }

        /// <summary>
        /// Analyzes breakout context: volume before/after breakout.
        /// </summary>
        public Dictionary<string, object> AnalyzeBreakoutContext(
            List<TickData> data, DateTime breakoutTime,
            int secondsBefore = 3, int secondsAfter = 3)
        {
            var tradeData = _cachedTradeData ?? data.Where(d => d.Type == "Trade").ToList();
            var beforeStart = breakoutTime.AddSeconds(-secondsBefore);
            var afterEnd = breakoutTime.AddSeconds(secondsAfter);

            var beforeTrades = tradeData.Where(t => t.Time >= beforeStart && t.Time < breakoutTime).ToList();
            var afterTrades = tradeData.Where(t => t.Time > breakoutTime && t.Time <= afterEnd).ToList();

            double beforeAvg = beforeTrades.Count > 0 ? beforeTrades.Average(t => t.Volume) : 0;
            double afterAvg = afterTrades.Count > 0 ? afterTrades.Average(t => t.Volume) : 0;

            return new Dictionary<string, object>
            {
                ["before_avg_volume"] = beforeAvg,
                ["after_avg_volume"] = afterAvg,
                ["before_total_volume"] = beforeTrades.Sum(t => t.Volume),
                ["after_total_volume"] = afterTrades.Sum(t => t.Volume),
                ["volume_amplification"] = beforeAvg > 0 ? afterAvg / beforeAvg : 1.0,
                ["before_trades_count"] = beforeTrades.Count,
                ["after_trades_count"] = afterTrades.Count
            };
        }

        // ===== Private helpers =====

        private static double SumAskVolumes(TickData row)
        {
            double sum = 0;
            foreach (var v in new[] { row.Ask1Volume, row.Ask2Volume, row.Ask3Volume, row.Ask4Volume, row.Ask5Volume })
                if (!double.IsNaN(v)) sum += v;
            return sum;
        }

        private static bool GetBool(Dictionary<string, object> d, string key, bool def)
        {
            if (d.TryGetValue(key, out var v) && v is bool b) return b;
            return def;
        }

        private static int GetInt(Dictionary<string, object> d, string key, int def)
        {
            if (d.TryGetValue(key, out var v))
            {
                if (v is int i) return i;
                if (v is long l) return (int)l;
                if (v is double dv) return (int)dv;
                if (int.TryParse(v?.ToString(), out int parsed)) return parsed;
            }
            return def;
        }

        private static double GetDouble(Dictionary<string, object> d, string key, double def)
        {
            if (d.TryGetValue(key, out var v))
            {
                if (v is double dv) return dv;
                if (v is float fv) return fv;
                if (v is int iv) return iv;
                if (v is long lv) return lv;
                if (double.TryParse(v?.ToString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsed)) return parsed;
            }
            return def;
        }
    }
}
