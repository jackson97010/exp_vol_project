using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace StrongestVwap.Strategy
{
    public class StrategyConfig
    {
        private readonly Dictionary<string, object> _config;

        public StrategyConfig(string configPath = "configs/strongest_vwap.yaml")
        {
            _config = GetDefaultConfig();

            if (!File.Exists(configPath))
            {
                Console.WriteLine($"[WARNING] Config not found: {configPath}, using defaults.");
                return;
            }

            try
            {
                var yaml = File.ReadAllText(configPath);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .Build();
                var root = deserializer.Deserialize<Dictionary<string, object>>(yaml);
                if (root == null) return;

                MergeSection(root, "strategy", _config);
                MergeSection(root, "signal_a", _config);
                MergeSection(root, "strong_group", _config);
                MergeSection(root, "order", _config);
                MergeSection(root, "exit", _config);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Failed to load config: {ex.Message}, using defaults.");
            }
        }

        private static void MergeSection(Dictionary<string, object> root, string sectionKey,
            Dictionary<string, object> target)
        {
            if (!root.ContainsKey(sectionKey)) return;
            if (root[sectionKey] is not Dictionary<object, object> raw) return;

            foreach (var kv in raw)
            {
                string key = kv.Key?.ToString() ?? "";
                if (!string.IsNullOrEmpty(key))
                    target[key] = kv.Value;
            }
        }

        private Dictionary<string, object> GetDefaultConfig()
        {
            return new Dictionary<string, object>
            {
                // [Strategy]
                ["market_rally_disable_threshold"] = 0.2,
                ["market_open_min_chg"] = 0.0,

                // [SignalA]
                ["signal_a_enabled"] = true,
                ["vwap_near_ratio"] = 1.008,
                ["bounce_ratio"] = 0.008,
                ["entry_start_time"] = new TimeSpan(9, 4, 0),
                ["entry_end_time"] = new TimeSpan(9, 25, 0),
                ["trade_zone_max_increase_ratio"] = 0.085,
                ["pre_condition_start_time"] = new TimeSpan(9, 4, 0),
                ["pre_condition_vwap_ratio"] = 0.997,

                // [StrongGroup]
                ["strong_group_enabled"] = true,
                ["member_min_month_trading_val"] = 200_000_000.0,
                ["group_min_month_trading_val"] = 3_000_000_000.0,
                ["group_min_avg_pct_chg"] = 0.01,
                ["group_min_val_ratio"] = 1.2,
                ["is_weighted_avg"] = false,
                ["group_valid_top_n"] = 20,
                ["top_group_rank_threshold"] = 10,
                ["top_group_max_select"] = 1,
                ["top_group_min_select"] = 1,
                ["normal_group_max_select"] = 1,
                ["normal_group_min_select"] = 1,
                ["entry_min_vwap_pct_chg"] = 0.04,
                ["require_raw_m1"] = true,
                ["member_cond1_enabled"] = false,
                ["member_cond2_enabled"] = false,
                ["member_cond4_enabled"] = false,
                ["member_strong_vol_ratio"] = 1.5,
                ["member_strong_trading_val"] = 2_000_000_000.0,
                ["member_vwap_pct_chg_threshold"] = 0.03,
                ["group_vol_ratio_exempt_threshold"] = 30_000_000_000.0,
                ["exclude_prev_limit_up_from_rank"] = false,
                ["exclude_disposition_from_rank"] = false,
                ["member_rank_field"] = "vwap",
                ["bypass_group_screening"] = false,
                ["require_bid_gt_ask"] = false,
                ["adaptive_mm_window_enabled"] = false,
                ["adaptive_mm_window_threshold"] = 10_000_000.0,
                ["adaptive_mm_window_seconds"] = 2,

                // [Order]
                ["position_cash"] = 10_000_000.0,
                ["disposition_stocks_enabled"] = true,
                ["filter_prev_day_limit_up"] = true,
                ["stop_loss_ratio_a"] = 0.995,
                ["stop_loss_enabled"] = true,
                ["bailout_ratio"] = 0.8,
                ["bailout_enabled"] = true,
                ["entry_time_limit"] = new TimeSpan(13, 0, 0),
                ["exit_time_limit"] = new TimeSpan(13, 20, 0),
                ["take_profit_splits"] = 3,
                ["take_profit_pcts"] = new List<double> { 0.01, 0.02, 0.03 },
                ["take_profit_ratios"] = new List<double>(),
                ["reserve_limit_up_splits"] = 2,
                ["reserve_limit_up_ratio"] = 0.4,
                ["max_entry_price"] = 1000.0,
                ["mode_e_exit"] = false,

                // [Trailing low exit]
                ["trailing_low_enabled"] = false,
                ["trailing_low_require_tp_fill"] = true,

                // [Rolling low 3-stage exit]
                ["rolling_low_exit_enabled"] = false,
                ["rolling_low_field1"] = "low_1m",
                ["rolling_low_field2"] = "low_3m",
                ["rolling_low_field3"] = "low_5m",

                // [Mode E: dynamic group selection]
                ["mode_e_enabled"] = false,
                ["mode_e_large_group_threshold"] = 5,
                ["mode_e_large_group_select"] = 3,
                ["mode_e_small_group_select"] = 2,
                ["mode_e_limit_up_cascade"] = true,
                ["mode_e_max_limit_up_skip"] = 5,

                // Paths
                ["output_path"] = Core.Constants.DefaultOutputDir,
                ["group_csv_path"] = Core.Constants.DefaultGroupCsvPath,
                ["tick_data_base_path"] = Core.Constants.TickDataBasePath
            };
        }

        public double GetDouble(string key, double defaultVal = 0)
        {
            if (_config.TryGetValue(key, out var v) && v != null)
            {
                if (v is double dv) return dv;
                if (v is float fv) return fv;
                if (v is int iv) return iv;
                if (v is long lv) return lv;
                if (double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                    return p;
            }
            return defaultVal;
        }

        public int GetInt(string key, int defaultVal = 0)
        {
            if (_config.TryGetValue(key, out var v) && v != null)
            {
                if (v is int iv) return iv;
                if (v is long lv) return (int)lv;
                if (v is double dv) return (int)dv;
                if (int.TryParse(v?.ToString(), out var p)) return p;
            }
            return defaultVal;
        }

        public bool GetBool(string key, bool defaultVal = false)
        {
            if (_config.TryGetValue(key, out var v) && v != null)
            {
                if (v is bool bv) return bv;
                string s = v.ToString()?.Trim().ToLowerInvariant() ?? "";
                if (s == "true" || s == "yes" || s == "1") return true;
                if (s == "false" || s == "no" || s == "0") return false;
            }
            return defaultVal;
        }

        public string GetString(string key, string defaultVal = "")
        {
            if (_config.TryGetValue(key, out var v) && v != null)
                return v.ToString() ?? defaultVal;
            return defaultVal;
        }

        public TimeSpan GetTimeSpan(string key, TimeSpan defaultVal)
        {
            if (_config.TryGetValue(key, out var v))
            {
                if (v is TimeSpan ts) return ts;
                if (TimeSpan.TryParse(v?.ToString(), out var parsed)) return parsed;
            }
            return defaultVal;
        }

        public List<double> GetDoubleList(string key)
        {
            if (_config.TryGetValue(key, out var v))
            {
                if (v is List<double> dl) return dl;
                if (v is List<object> ol)
                {
                    var result = new List<double>();
                    foreach (var item in ol)
                    {
                        if (double.TryParse(item?.ToString(), NumberStyles.Any,
                            CultureInfo.InvariantCulture, out var d))
                            result.Add(d);
                    }
                    return result;
                }
            }
            return new List<double>();
        }

        public Dictionary<string, object> RawConfig => _config;
    }
}
