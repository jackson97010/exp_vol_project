using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BacktestModule.Strategy
{
    /// <summary>
    /// YAML-based strategy configuration loader.
    /// Mirrors Python StrategyConfig: loads YAML, merges with defaults, provides typed access.
    /// </summary>
    public class StrategyConfig
    {
        private readonly Dictionary<string, object> _config;
        private readonly string _configPath;

        public StrategyConfig(string configPath = "Bo_v2.yaml")
        {
            _configPath = configPath;
            _config = LoadConfig();
        }

        private Dictionary<string, object> LoadConfig()
        {
            var config = GetDefaultConfig();

            if (!File.Exists(_configPath))
            {
                Console.WriteLine($"[WARNING] Config file not found: {_configPath}, using defaults.");
                return config;
            }

            try
            {
                var yaml = File.ReadAllText(_configPath);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .Build();
                var root = deserializer.Deserialize<Dictionary<string, object>>(yaml);

                if (root == null) return config;

                // Extract the 'strategy' section
                Dictionary<string, object> strategy = new Dictionary<string, object>();
                if (root.ContainsKey("strategy") && root["strategy"] is Dictionary<object, object> rawStrategy)
                {
                    foreach (var kv in rawStrategy)
                        strategy[kv.Key?.ToString() ?? ""] = kv.Value;
                }

                // Time settings
                config["entry_start_time"] = ParseTime(
                    GetString(strategy, "entry_start_time", "09:05:00"),
                    (TimeSpan)config["entry_start_time"]);
                config["entry_cutoff_time"] = ParseTime(
                    GetString(strategy, "entry_cutoff_time", "13:00:00"),
                    (TimeSpan)config["entry_cutoff_time"]);

                // Entry conditions
                config["entry_cooldown"] = GetDouble(strategy, "entry_cooldown", 30.0);
                config["ask_bid_ratio_threshold"] = GetDouble(strategy, "ask_bid_ratio_threshold", 1.0);
                config["massive_matching_enabled"] = GetBool(strategy, "massive_matching_enabled", true);
                config["massive_matching_amount"] = GetDouble(strategy, "massive_matching_amount", 1000000.0);

                // Dynamic liquidity threshold
                config["use_dynamic_liquidity_threshold"] = GetBool(strategy, "use_dynamic_liquidity_threshold", false);
                config["dynamic_liquidity_threshold_file"] = GetString(strategy, "dynamic_liquidity_threshold_file", "daily_liquidity_threshold.parquet");
                config["dynamic_liquidity_multiplier"] = GetDouble(strategy, "dynamic_liquidity_multiplier", 0.004);
                config["dynamic_liquidity_threshold_cap"] = GetDouble(strategy, "dynamic_liquidity_threshold_cap", 50_000_000);

                config["ratio_entry_enabled"] = GetBool(strategy, "ratio_entry_enabled", true);
                config["ratio_entry_threshold"] = GetDouble(strategy, "ratio_entry_threshold", 3.0);
                config["ratio_column"] = GetString(strategy, "ratio_column", "ratio_15s_300s");

                // Price change limit
                config["price_change_limit_enabled"] = GetBool(strategy, "price_change_limit_enabled", true);
                config["price_change_limit_pct"] = GetDouble(strategy, "price_change_limit_pct", 8.5);

                // Interval percentage filter
                config["interval_pct_filter_enabled"] = GetBool(strategy, "interval_pct_filter_enabled", false);
                int minutes = GetInt(strategy, "interval_pct_minutes", 3);
                config["interval_pct_minutes"] = (minutes == 2 || minutes == 3 || minutes == 5) ? minutes : 3;
                config["interval_pct_threshold"] = GetDouble(strategy, "interval_pct_threshold", 4.0);

                // Entry buffer
                config["entry_buffer_enabled"] = GetBool(strategy, "entry_buffer_enabled", false);
                config["entry_buffer_milliseconds"] = GetInt(strategy, "entry_buffer_milliseconds", 20);

                // Above open check
                config["above_open_check_enabled"] = GetBool(strategy, "above_open_check_enabled", true);

                // 10-minute low filter
                config["low_10min_filter_enabled"] = GetBool(strategy, "low_10min_filter_enabled", false);
                config["low_10min_threshold"] = GetDouble(strategy, "low_10min_threshold", 4.0);

                // Breakout quality
                config["breakout_quality_check_enabled"] = GetBool(strategy, "breakout_quality_check_enabled", false);
                config["breakout_min_volume"] = GetInt(strategy, "breakout_min_volume", 10);
                config["breakout_min_ask_eat_ratio"] = GetDouble(strategy, "breakout_min_ask_eat_ratio", 0.25);
                config["breakout_absolute_large_volume"] = GetInt(strategy, "breakout_absolute_large_volume", 50);

                // Small order filter
                config["small_order_filter_enabled"] = GetBool(strategy, "small_order_filter_enabled", false);
                config["small_order_check_trades"] = GetInt(strategy, "small_order_check_trades", 30);
                config["small_order_threshold"] = GetInt(strategy, "small_order_threshold", 3);
                config["tiny_order_threshold"] = GetInt(strategy, "tiny_order_threshold", 2);
                config["single_order_threshold"] = GetInt(strategy, "single_order_threshold", 1);
                config["small_order_ratio_limit"] = GetDouble(strategy, "small_order_ratio_limit", 0.8);
                config["tiny_order_ratio_limit"] = GetDouble(strategy, "tiny_order_ratio_limit", 0.5);
                config["single_order_ratio_limit"] = GetDouble(strategy, "single_order_ratio_limit", 0.4);
                config["require_large_order_confirmation"] = GetBool(strategy, "require_large_order_confirmation", false);
                config["large_order_threshold"] = GetInt(strategy, "large_order_threshold", 20);
                config["min_large_orders"] = GetInt(strategy, "min_large_orders", 2);

                // Reentry
                config["reentry"] = GetBool(strategy, "reentry", false);
                config["time_settings"] = GetString(strategy, "time_settings", "1s");

                // Output path
                config["output_path"] = GetString(strategy, "output_path", @"D:/回測結果");

                // Ratio increase after loss
                config["ratio_increase_after_loss_enabled"] = GetBool(strategy, "ratio_increase_after_loss_enabled", false);
                config["ratio_increase_multiplier"] = GetDouble(strategy, "ratio_increase_multiplier", 1.0);
                config["ratio_increase_min_threshold"] = GetDouble(strategy, "ratio_increase_min_threshold", 6.0);

                // Stop-loss parameters
                config["strategy_b_stop_loss_ticks_small"] = GetInt(strategy, "strategy_b_stop_loss_ticks_small", 4);
                config["strategy_b_stop_loss_ticks_large"] = GetInt(strategy, "strategy_b_stop_loss_ticks_large", 3);
                config["momentum_stop_loss_seconds"] = GetDouble(strategy, "momentum_stop_loss_seconds", 15.0);
                config["momentum_stop_loss_seconds_extended"] = GetDouble(strategy, "momentum_stop_loss_seconds_extended", 30.0);
                config["momentum_stop_loss_amount"] = GetDouble(strategy, "momentum_stop_loss_amount", 5000000.0);
                config["momentum_extended_capital_threshold"] = GetDouble(strategy, "momentum_extended_capital_threshold", 50000000000);
                config["momentum_extended_price_min"] = GetDouble(strategy, "momentum_extended_price_min", 100.0);
                config["momentum_extended_price_max"] = GetDouble(strategy, "momentum_extended_price_max", 500.0);

                // Trailing stop configuration
                config["trailing_stop"] = ParseTrailingStopConfig(strategy);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Failed to load config: {ex.Message}, using defaults.");
            }

            return config;
        }

        private Dictionary<string, object> ParseTrailingStopConfig(Dictionary<string, object> strategy)
        {
            var result = new Dictionary<string, object>
            {
                ["enabled"] = false,
                ["levels"] = new List<Dictionary<string, object>>(),
                ["entry_price_protection"] = true
            };

            if (!strategy.ContainsKey("trailing_stop")) return result;

            if (strategy["trailing_stop"] is Dictionary<object, object> tsRaw)
            {
                var ts = new Dictionary<string, object>();
                foreach (var kv in tsRaw)
                    ts[kv.Key?.ToString() ?? ""] = kv.Value;

                result["enabled"] = GetBool(ts, "enabled", false);
                result["entry_price_protection"] = GetBool(ts, "entry_price_protection", true);

                var levels = new List<Dictionary<string, object>>();
                if (ts.ContainsKey("levels") && ts["levels"] is List<object> rawLevels)
                {
                    foreach (var rawLevel in rawLevels)
                    {
                        if (rawLevel is Dictionary<object, object> levelDict)
                        {
                            var level = new Dictionary<string, object>();
                            foreach (var kv in levelDict)
                                level[kv.Key?.ToString() ?? ""] = kv.Value;

                            levels.Add(new Dictionary<string, object>
                            {
                                ["name"] = GetString(level, "name", ""),
                                ["field"] = GetString(level, "field", ""),
                                ["exit_ratio"] = GetDouble(level, "exit_ratio", 0.333)
                            });
                        }
                    }
                }
                result["levels"] = levels;
            }

            return result;
        }

        private Dictionary<string, object> GetDefaultConfig()
        {
            return new Dictionary<string, object>
            {
                ["entry_start_time"] = new TimeSpan(9, 9, 0),
                ["entry_cutoff_time"] = new TimeSpan(13, 0, 0),
                ["entry_cooldown"] = 30.0,
                ["ask_bid_ratio_threshold"] = 1.0,
                ["massive_matching_enabled"] = true,
                ["massive_matching_amount"] = 1000000.0,
                ["use_dynamic_liquidity_threshold"] = false,
                ["dynamic_liquidity_threshold_file"] = "daily_liquidity_threshold.parquet",
                ["dynamic_liquidity_multiplier"] = 0.004,
                ["dynamic_liquidity_threshold_cap"] = 50_000_000.0,
                ["ratio_entry_enabled"] = true,
                ["ratio_entry_threshold"] = 3.0,
                ["ratio_column"] = "ratio_15s_300s",
                ["price_change_limit_enabled"] = true,
                ["price_change_limit_pct"] = 8.5,
                ["interval_pct_filter_enabled"] = false,
                ["interval_pct_minutes"] = 3,
                ["interval_pct_threshold"] = 4.0,
                ["entry_buffer_enabled"] = false,
                ["entry_buffer_milliseconds"] = 20,
                ["above_open_check_enabled"] = true,
                ["low_10min_filter_enabled"] = false,
                ["low_10min_threshold"] = 4.0,
                ["breakout_quality_check_enabled"] = false,
                ["breakout_min_volume"] = 10,
                ["breakout_min_ask_eat_ratio"] = 0.25,
                ["breakout_absolute_large_volume"] = 50,
                ["small_order_filter_enabled"] = false,
                ["small_order_check_trades"] = 30,
                ["small_order_threshold"] = 3,
                ["tiny_order_threshold"] = 2,
                ["single_order_threshold"] = 1,
                ["small_order_ratio_limit"] = 0.8,
                ["tiny_order_ratio_limit"] = 0.5,
                ["single_order_ratio_limit"] = 0.4,
                ["require_large_order_confirmation"] = false,
                ["large_order_threshold"] = 20,
                ["min_large_orders"] = 2,
                ["reentry"] = false,
                ["time_settings"] = "1s",
                ["output_path"] = @"D:/回測結果",
                ["ratio_increase_after_loss_enabled"] = false,
                ["ratio_increase_multiplier"] = 1.0,
                ["ratio_increase_min_threshold"] = 6.0,
                ["strategy_b_stop_loss_ticks_small"] = 4,
                ["strategy_b_stop_loss_ticks_large"] = 3,
                ["momentum_stop_loss_seconds"] = 15.0,
                ["momentum_stop_loss_seconds_extended"] = 30.0,
                ["momentum_stop_loss_amount"] = 5000000.0,
                ["momentum_extended_capital_threshold"] = 50000000000.0,
                ["momentum_extended_price_min"] = 100.0,
                ["momentum_extended_price_max"] = 500.0,
                ["trailing_stop"] = new Dictionary<string, object>
                {
                    ["enabled"] = false,
                    ["levels"] = new List<Dictionary<string, object>>(),
                    ["entry_price_protection"] = true
                }
            };
        }

        private static TimeSpan ParseTime(string value, TimeSpan fallback)
        {
            if (TimeSpan.TryParseExact(value, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var ts))
                return ts;
            if (TimeSpan.TryParse(value, out ts))
                return ts;
            return fallback;
        }

        // ===== Helper methods for extracting typed values from YAML dict =====

        private static string GetString(Dictionary<string, object> d, string key, string defaultVal)
        {
            if (d.TryGetValue(key, out var v) && v != null)
                return v.ToString() ?? defaultVal;
            return defaultVal;
        }

        private static double GetDouble(Dictionary<string, object> d, string key, double defaultVal)
        {
            if (d.TryGetValue(key, out var v) && v != null)
            {
                if (v is double dv) return dv;
                if (v is float fv) return fv;
                if (v is int iv) return iv;
                if (v is long lv) return lv;
                if (double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
            return defaultVal;
        }

        private static int GetInt(Dictionary<string, object> d, string key, int defaultVal)
        {
            if (d.TryGetValue(key, out var v) && v != null)
            {
                if (v is int iv) return iv;
                if (v is long lv) return (int)lv;
                if (v is double dv) return (int)dv;
                if (int.TryParse(v.ToString(), out var parsed))
                    return parsed;
            }
            return defaultVal;
        }

        private static bool GetBool(Dictionary<string, object> d, string key, bool defaultVal)
        {
            if (d.TryGetValue(key, out var v) && v != null)
            {
                if (v is bool bv) return bv;
                if (bool.TryParse(v.ToString(), out var parsed))
                    return parsed;
                // YAML "true"/"false" strings
                string s = v.ToString()?.Trim().ToLowerInvariant() ?? "";
                if (s == "true" || s == "yes" || s == "1") return true;
                if (s == "false" || s == "no" || s == "0") return false;
            }
            return defaultVal;
        }

        // ===== Public API: get config sections =====

        public Dictionary<string, object> GetEntryConfig()
        {
            var keys = new[]
            {
                "entry_start_time", "entry_cutoff_time", "entry_cooldown",
                "above_open_check_enabled", "ask_bid_ratio_threshold",
                "massive_matching_enabled", "massive_matching_amount",
                "use_dynamic_liquidity_threshold", "dynamic_liquidity_threshold_file",
                "dynamic_liquidity_multiplier", "dynamic_liquidity_threshold_cap",
                "ratio_entry_enabled", "ratio_entry_threshold", "ratio_column",
                "price_change_limit_enabled", "price_change_limit_pct",
                "interval_pct_filter_enabled", "interval_pct_minutes", "interval_pct_threshold",
                "entry_buffer_enabled", "entry_buffer_milliseconds",
                "low_10min_filter_enabled", "low_10min_threshold",
                "breakout_quality_check_enabled", "breakout_min_volume",
                "breakout_min_ask_eat_ratio", "breakout_absolute_large_volume",
                "small_order_filter_enabled", "small_order_check_trades",
                "small_order_threshold", "tiny_order_threshold", "single_order_threshold",
                "small_order_ratio_limit", "tiny_order_ratio_limit", "single_order_ratio_limit",
                "require_large_order_confirmation", "large_order_threshold", "min_large_orders",
                "ratio_increase_after_loss_enabled", "ratio_increase_multiplier",
                "ratio_increase_min_threshold"
            };
            return ExtractKeys(keys);
        }

        public Dictionary<string, object> GetExitConfig()
        {
            var keys = new[]
            {
                "strategy_b_stop_loss_ticks_small", "strategy_b_stop_loss_ticks_large",
                "momentum_stop_loss_seconds", "momentum_stop_loss_seconds_extended",
                "momentum_stop_loss_amount", "momentum_extended_capital_threshold",
                "momentum_extended_price_min", "momentum_extended_price_max",
                "trailing_stop"
            };
            return ExtractKeys(keys);
        }

        public Dictionary<string, object> GetReentryConfig()
        {
            var keys = new[]
            {
                "reentry", "time_settings",
                "ratio_increase_after_loss_enabled",
                "ratio_increase_multiplier", "ratio_increase_min_threshold"
            };
            return ExtractKeys(keys);
        }

        public Dictionary<string, object> GetAllConfig()
        {
            return new Dictionary<string, object>(_config);
        }

        /// <summary>
        /// Direct access to underlying config for parameter overrides.
        /// </summary>
        public Dictionary<string, object> RawConfig => _config;

        private Dictionary<string, object> ExtractKeys(string[] keys)
        {
            var result = new Dictionary<string, object>();
            foreach (var key in keys)
            {
                if (_config.ContainsKey(key))
                    result[key] = _config[key];
            }
            return result;
        }
    }
}
