using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BacktestModule.Core.Models;

namespace BacktestModule.Strategy
{
    /// <summary>
    /// Entry signal record.
    /// </summary>
    public class EntrySignal
    {
        public DateTime Time { get; set; }
        public string StockId { get; set; }
        public double Price { get; set; }
        public double DayHigh { get; set; }
        public bool Passed { get; set; }
        public Dictionary<string, Dictionary<string, object>> Conditions { get; set; } = new();
        public string FailureReason { get; set; } = "";

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>
            {
                ["time"] = Time.ToString("HH:mm:ss.fff"),
                ["stock_id"] = StockId,
                ["price"] = Price,
                ["day_high"] = DayHigh,
                ["passed"] = Passed,
                ["conditions"] = Conditions,
                ["failure_reason"] = FailureReason
            };
        }
    }

    /// <summary>
    /// Entry Checker: checks all entry conditions in order.
    /// Each condition is checked sequentially; if any fails, the signal is rejected early.
    /// </summary>
    public class EntryChecker
    {
        private readonly Dictionary<string, object> _config;
        public List<EntrySignal> EntrySignals { get; } = new();
        public SmallOrderFilter SmallOrderFilter { get; }

        public EntryChecker(Dictionary<string, object> config)
        {
            _config = config;
            SmallOrderFilter = new SmallOrderFilter(config);
        }

        /// <summary>
        /// Checks if there is a Day High breakout.
        /// </summary>
        public bool CheckDayHighBreakout(double currentDayHigh, double prevDayHigh)
        {
            return currentDayHigh > prevDayHigh && prevDayHigh > 0;
        }

        /// <summary>
        /// Main entry check: checks ALL entry conditions in sequence.
        /// Returns an EntrySignal with Passed=true if all conditions met.
        /// </summary>
        public EntrySignal CheckEntrySignals(
            string stockId,
            double currentPrice,
            DateTime currentTime,
            double prevDayHigh,
            Dictionary<string, double> indicators,
            double askBidRatio,
            double refPrice,
            double massiveMatchingAmount = 0.0,
            double? fixedRatioThreshold = null,
            double? dynamicRatioThreshold = null,
            double? minOutsideAmount = null,
            bool forceLog = false,
            List<TickData> tickData = null,
            TickData currentRow = null,
            bool isDayHighBreakout = false,
            DateTime? lastExitTime = null)
        {
            var signal = new EntrySignal
            {
                Time = currentTime,
                StockId = stockId,
                Price = currentPrice,
                DayHigh = prevDayHigh,
                Passed = false
            };

            bool shouldLog = false;  // Set to true for detailed entry debugging

            // 0. Time check
            var entryStartTime = GetTimeSpan("entry_start_time", new TimeSpan(9, 9, 0));
            if (currentTime.TimeOfDay < entryStartTime)
            {
                signal.FailureReason = "Before entry start time";
                EntrySignals.Add(signal);
                return signal;
            }

            var cutoffTime = GetTimeSpan("entry_cutoff_time", new TimeSpan(13, 0, 0));
            if (currentTime.TimeOfDay >= cutoffTime)
            {
                signal.FailureReason = "Past entry cutoff time";
                EntrySignals.Add(signal);
                return signal;
            }

            // 0.5 Cooldown check
            if (lastExitTime.HasValue)
            {
                double timeSinceExit = (currentTime - lastExitTime.Value).TotalSeconds;
                double cooldownPeriod = GetDouble("entry_cooldown", 30.0);
                if (timeSinceExit < cooldownPeriod)
                {
                    signal.FailureReason = "Within exit cooldown period";
                    EntrySignals.Add(signal);
                    return signal;
                }
            }

            // 1. Above-open check (optional)
            double priceChangePct = refPrice > 0
                ? (currentPrice - refPrice) / refPrice * 100.0
                : 0.0;

            bool aboveOpenEnabled = GetBool("above_open_check_enabled", false);
            if (aboveOpenEnabled && currentPrice <= refPrice)
            {
                signal.FailureReason = "Below reference price (open)";
                EntrySignals.Add(signal);
                return signal;
            }

            // 2. Price change limit (default 8.5%)
            double priceLimit = GetDouble("price_change_limit_pct", 8.5);
            bool priceLimitEnabled = GetBool("price_change_limit_enabled", true);
            if (priceLimitEnabled && priceChangePct > priceLimit)
            {
                signal.FailureReason = $"Price change exceeds {priceLimit}%";
                EntrySignals.Add(signal);
                return signal;
            }

            // 2b. Min price change filter (minimum required gain from yesterday close)
            bool minPriceChangeEnabled = GetBool("min_price_change_enabled", false);
            if (minPriceChangeEnabled)
            {
                double minPriceChangePct = GetDouble("min_price_change_pct", 0.0);
                if (priceChangePct < minPriceChangePct)
                {
                    signal.FailureReason = $"Price change {priceChangePct:F1}% below minimum {minPriceChangePct}%";
                    EntrySignals.Add(signal);
                    return signal;
                }
            }

            // 2c. MA5 bias threshold (price must be >= MA5 × 1.05 threshold)
            bool ma5BiasEnabled = GetBool("ma5_bias5_enabled", false);
            if (ma5BiasEnabled)
            {
                double thresholdPrice = GetDouble("ma5_bias5_threshold_price", 0.0);
                if (thresholdPrice > 0 && currentPrice < thresholdPrice)
                {
                    signal.FailureReason = $"Price {currentPrice:F2} below MA5 bias threshold {thresholdPrice:F2}";
                    EntrySignals.Add(signal);
                    return signal;
                }
            }

            // 2d. Low ratio filter (low_3m / low_15m > threshold)
            bool lowRatioEnabled = GetBool("low_ratio_filter_enabled", false);
            if (lowRatioEnabled)
            {
                double lowRatioThreshold = GetDouble("low_ratio_threshold", 1.005);
                double low3m = indicators.TryGetValue("low_3min", out var l3) ? l3 : 0.0;
                double low15m = indicators.TryGetValue("low_15min", out var l15) ? l15 : 0.0;
                if (low3m > 0 && low15m > 0)
                {
                    double lowRatio = low3m / low15m;
                    if (lowRatio <= lowRatioThreshold)
                    {
                        signal.FailureReason = $"Low ratio {lowRatio:F4} <= {lowRatioThreshold} (low_3m={low3m:F2}/low_15m={low15m:F2})";
                        EntrySignals.Add(signal);
                        return signal;
                    }
                }
            }

            // 3. Order book ratio (ask/bid >= threshold)
            double askBidThreshold = GetDouble("ask_bid_ratio_threshold", 1.0);
            if (askBidRatio < askBidThreshold)
            {
                signal.FailureReason = "Order book ratio insufficient";
                EntrySignals.Add(signal);
                return signal;
            }

            // 4. Massive matching condition
            if (GetBool("massive_matching_enabled", true))
            {
                double massiveThreshold = GetDouble("massive_matching_amount", 50000000.0);  // 50M to match Python Bo_v2.yaml
                // Dynamic threshold support: when enabled, use the dynamically loaded threshold
                // The dynamic threshold is loaded by BacktestEngine/ConfigLoader from parquet
                // and stored in config as "dynamic_liquidity_resolved_threshold"
                bool useDynamic = GetBool("use_dynamic_liquidity_threshold", false);
                if (useDynamic)
                {
                    double dynamicThreshold = GetDouble("dynamic_liquidity_resolved_threshold", 0.0);
                    if (dynamicThreshold > 0)
                    {
                        massiveThreshold = dynamicThreshold;
                    }
                }

                if (massiveMatchingAmount < massiveThreshold)
                {
                    signal.FailureReason = $"Massive matching amount insufficient ({massiveMatchingAmount/1000000:F2}M < {massiveThreshold/1000000:F0}M)";
                    EntrySignals.Add(signal);
                    return signal;
                }
            }

            // 4b. Outside amount comparison (reentry only)
            if (minOutsideAmount.HasValue)
            {
                if (massiveMatchingAmount <= minOutsideAmount.Value)
                {
                    signal.FailureReason = "Outside amount insufficient (reentry)";
                    EntrySignals.Add(signal);
                    return signal;
                }
            }

            // 5. Ratio condition (skip before ratio_skip_before_time if configured)
            if (GetBool("ratio_entry_enabled", true))
            {
                var ratioSkipBefore = GetTimeSpan("ratio_skip_before_time", TimeSpan.Zero);
                bool skipRatio = ratioSkipBefore > TimeSpan.Zero && currentTime.TimeOfDay < ratioSkipBefore;

                if (!skipRatio)
                {
                    double ratio = indicators.TryGetValue("ratio", out var r) ? r : 0.0;
                    if (double.IsNaN(ratio)) ratio = 0.0;

                    if (dynamicRatioThreshold.HasValue && fixedRatioThreshold.HasValue)
                    {
                        // OR logic: ratio > fixed OR ratio > dynamic
                        bool ratioPass = (ratio > fixedRatioThreshold.Value) || (ratio > dynamicRatioThreshold.Value);
                        if (!ratioPass)
                        {
                            signal.FailureReason = "Ratio condition not met";
                            EntrySignals.Add(signal);
                            return signal;
                        }
                    }
                    else
                    {
                        double defaultThreshold = GetDouble("ratio_entry_threshold", 3.0);
                        if (ratio < defaultThreshold)
                        {
                            signal.FailureReason = "Ratio condition not met";
                            EntrySignals.Add(signal);
                            return signal;
                        }
                    }
                }
            }

            // 6. Interval percentage filter
            if (GetBool("interval_pct_filter_enabled", true))
            {
                int intervalMinutes = GetInt("interval_pct_minutes", 5);
                double intervalThreshold = GetDouble("interval_pct_threshold", 3.0);
                string pctKey = $"pct_{intervalMinutes}min";
                double intervalPct = indicators.TryGetValue(pctKey, out var p) ? p : 0.0;
                if (double.IsNaN(intervalPct)) intervalPct = 0.0;

                if (intervalPct > intervalThreshold)
                {
                    signal.FailureReason = $"{intervalMinutes}-min price change too large";
                    EntrySignals.Add(signal);
                    return signal;
                }
            }

            // 7. Low point filters (3min / 10min / 15min)
            if (GetBool("low_3min_filter_enabled", false))
            {
                double threshold = GetDouble("low_3min_threshold", 2.0);
                double low3min = indicators.TryGetValue("low_3min", out var l) ? l : currentPrice;
                if (low3min > 0)
                {
                    double fromLow = (currentPrice - low3min) / low3min * 100.0;
                    if (fromLow > threshold)
                    {
                        signal.FailureReason = "Too far from 3-min low";
                        EntrySignals.Add(signal);
                        return signal;
                    }
                }
            }
            else if (GetBool("low_10min_filter_enabled", false))
            {
                double threshold = GetDouble("low_10min_threshold", 4.0);
                double low10min = indicators.TryGetValue("low_10min", out var l) ? l : currentPrice;
                if (low10min > 0)
                {
                    double fromLow = (currentPrice - low10min) / low10min * 100.0;
                    if (fromLow > threshold)
                    {
                        signal.FailureReason = "Too far from 10-min low";
                        EntrySignals.Add(signal);
                        return signal;
                    }
                }
            }
            else if (GetBool("low_15min_filter_enabled", false))
            {
                double threshold = GetDouble("low_15min_threshold", 4.0);
                double low15min = indicators.TryGetValue("low_15min", out var l) ? l : currentPrice;
                if (low15min > 0)
                {
                    double fromLow = (currentPrice - low15min) / low15min * 100.0;
                    if (fromLow > threshold)
                    {
                        signal.FailureReason = "Too far from 15-min low";
                        EntrySignals.Add(signal);
                        return signal;
                    }
                }
            }

            // 8. Non-limit-up check (price change < 10%)
            if (priceChangePct >= 10.0)
            {
                signal.FailureReason = "Near limit-up";
                EntrySignals.Add(signal);
                return signal;
            }

            // 9. Breakout quality / small order filter
            if (SmallOrderFilter.BreakoutQualityEnabled && currentRow != null && isDayHighBreakout)
            {
                var (isValid, reason) = SmallOrderFilter.CheckBreakoutQuality(currentRow, isDayHighBreakout, tickData);
                if (!isValid)
                {
                    signal.FailureReason = $"Breakout quality: {reason}";
                    EntrySignals.Add(signal);
                    return signal;
                }
            }
            else if (SmallOrderFilter.Enabled && tickData != null)
            {
                var (isValid, reason) = SmallOrderFilter.CheckSmallOrderPattern(tickData, currentTime);
                if (!isValid)
                {
                    signal.FailureReason = $"Small order: {reason}";
                    EntrySignals.Add(signal);
                    return signal;
                }
            }

            // All conditions passed
            signal.Passed = true;
            signal.FailureReason = "";
            EntrySignals.Add(signal);

            if (shouldLog)
            {
                System.Console.WriteLine("Entry check result: ALL CONDITIONS PASSED!");
            }

            return signal;
        }

        /// <summary>
        /// Gets a summary of all entry signals.
        /// </summary>
        public void PrintEntrySignalsSummary()
        {
            if (EntrySignals.Count == 0) return;

            int totalChecks = EntrySignals.Count;
            int passed = EntrySignals.Count(s => s.Passed);

            System.Console.WriteLine($"\n{"",60}");
            System.Console.WriteLine("Entry Signal Summary");
            System.Console.WriteLine($"Total checks: {totalChecks}");
            System.Console.WriteLine($"Passed: {passed}");
            System.Console.WriteLine($"Pass rate: {(double)passed / totalChecks * 100:F1}%");

            var failureStats = EntrySignals
                .Where(s => !s.Passed && !string.IsNullOrEmpty(s.FailureReason))
                .GroupBy(s => s.FailureReason)
                .OrderByDescending(g => g.Count())
                .ToList();

            if (failureStats.Count > 0)
            {
                System.Console.WriteLine("\nFailure reasons:");
                foreach (var group in failureStats)
                {
                    System.Console.WriteLine($"  {group.Key}: {group.Count()} ({(double)group.Count() / totalChecks * 100:F1}%)");
                }
            }
        }

        // ===== Config access helpers =====

        private TimeSpan GetTimeSpan(string key, TimeSpan defaultVal)
        {
            if (_config.TryGetValue(key, out var v))
            {
                if (v is TimeSpan ts) return ts;
                if (v is DateTime dt) return dt.TimeOfDay;
                if (v is string s && TimeSpan.TryParse(s, out ts)) return ts;
            }
            return defaultVal;
        }

        private double GetDouble(string key, double defaultVal)
        {
            if (_config.TryGetValue(key, out var v))
            {
                if (v is double d) return d;
                if (v is float f) return f;
                if (v is int i) return i;
                if (v is long l) return l;
                if (double.TryParse(v?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double p)) return p;
            }
            return defaultVal;
        }

        private int GetInt(string key, int defaultVal)
        {
            if (_config.TryGetValue(key, out var v))
            {
                if (v is int i) return i;
                if (v is long l) return (int)l;
                if (v is double d) return (int)d;
                if (int.TryParse(v?.ToString(), out int p)) return p;
            }
            return defaultVal;
        }

        private bool GetBool(string key, bool defaultVal)
        {
            if (_config.TryGetValue(key, out var v))
            {
                if (v is bool b) return b;
                if (bool.TryParse(v?.ToString(), out bool p)) return p;
            }
            return defaultVal;
        }
    }
}
