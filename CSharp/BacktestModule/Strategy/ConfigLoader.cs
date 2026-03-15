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

        public StrategyConfig(string configPath = "configs/Bo_v2.yaml")
        {
            _configPath = configPath;
            _config = LoadConfig();
        }

        private Dictionary<string, object> LoadConfig()
        {
            var config = GetDefaultConfig();

            if (!File.Exists(_configPath))
            {
                System.Console.WriteLine($"[WARNING] Config file not found: {_configPath}, using defaults.");
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
                    GetString(strategy, "entry_start_time", "09:09:00"),
                    (TimeSpan)config["entry_start_time"]);
                config["entry_cutoff_time"] = ParseTime(
                    GetString(strategy, "entry_cutoff_time", "13:00:00"),
                    (TimeSpan)config["entry_cutoff_time"]);

                // Entry conditions
                config["entry_cooldown"] = GetDouble(strategy, "entry_cooldown", 30.0);
                config["ask_bid_ratio_threshold"] = GetDouble(strategy, "ask_bid_ratio_threshold", 1.0);
                config["massive_matching_enabled"] = GetBool(strategy, "massive_matching_enabled", true);
                config["massive_matching_amount"] = GetDouble(strategy, "massive_matching_amount", 50000000.0);  // 50M to match Python Bo_v2.yaml
                config["massive_matching_window"] = GetInt(strategy, "massive_matching_window", 1);

                // Dynamic liquidity threshold (match Python: default off, multiplier 0.004)
                config["use_dynamic_liquidity_threshold"] = GetBool(strategy, "use_dynamic_liquidity_threshold", false);
                config["dynamic_liquidity_threshold_file"] = GetString(strategy, "dynamic_liquidity_threshold_file", "daily_liquidity_threshold.parquet");
                config["dynamic_liquidity_multiplier"] = GetDouble(strategy, "dynamic_liquidity_multiplier", 0.004);
                config["dynamic_liquidity_threshold_cap"] = GetDouble(strategy, "dynamic_liquidity_threshold_cap", 50_000_000);

                config["ratio_entry_enabled"] = GetBool(strategy, "ratio_entry_enabled", true);
                config["ratio_entry_threshold"] = GetDouble(strategy, "ratio_entry_threshold", 3.0);
                config["ratio_skip_before_time"] = GetString(strategy, "ratio_skip_before_time", "");
                config["ratio_column"] = GetString(strategy, "ratio_column", "ratio_15s_300s");

                // Price change limit
                config["price_change_limit_enabled"] = GetBool(strategy, "price_change_limit_enabled", true);
                config["price_change_limit_pct"] = GetDouble(strategy, "price_change_limit_pct", 8.5);

                // Min price change filter (minimum required gain from yesterday close)
                config["min_price_change_enabled"] = GetBool(strategy, "min_price_change_enabled", false);
                config["min_price_change_pct"] = GetDouble(strategy, "min_price_change_pct", 0.0);

                // MA5 bias threshold (price must be >= MA5 × 1.05 threshold from parquet)
                config["ma5_bias5_enabled"] = GetBool(strategy, "ma5_bias5_enabled", false);
                config["ma5_bias5_file"] = GetString(strategy, "ma5_bias5_file", "ma5_bias5_threshold.parquet");

                // Low ratio filter (low_3m / low_15m > threshold)
                config["low_ratio_filter_enabled"] = GetBool(strategy, "low_ratio_filter_enabled", false);
                config["low_ratio_threshold"] = GetDouble(strategy, "low_ratio_threshold", 1.005);

                // Interval percentage filter
                config["interval_pct_filter_enabled"] = GetBool(strategy, "interval_pct_filter_enabled", true);  // Match Python: true
                int minutes = GetInt(strategy, "interval_pct_minutes", 5);  // Match Python: 5
                config["interval_pct_minutes"] = (minutes == 2 || minutes == 3 || minutes == 5) ? minutes : 5;
                config["interval_pct_threshold"] = GetDouble(strategy, "interval_pct_threshold", 3.0);  // Match Python: 3.0

                // Entry buffer
                config["entry_buffer_enabled"] = GetBool(strategy, "entry_buffer_enabled", true);  // Must be true to allow entry after breakout
                config["entry_buffer_milliseconds"] = GetInt(strategy, "entry_buffer_milliseconds", 100);  // Match Python: 100ms

                // Above open check
                config["above_open_check_enabled"] = GetBool(strategy, "above_open_check_enabled", false);

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
                config["time_settings"] = GetString(strategy, "time_settings", "3s");

                // Output path
                config["output_path"] = GetString(strategy, "output_path", @"D:/回測結果");

                // Ratio increase after loss
                config["ratio_increase_after_loss_enabled"] = GetBool(strategy, "ratio_increase_after_loss_enabled", true);
                config["ratio_increase_multiplier"] = GetDouble(strategy, "ratio_increase_multiplier", 0.8);
                config["ratio_increase_min_threshold"] = GetDouble(strategy, "ratio_increase_min_threshold", 6.0);

                // Stop-loss parameters
                config["strategy_b_stop_loss_ticks_small"] = GetInt(strategy, "strategy_b_stop_loss_ticks_small", 3);
                config["strategy_b_stop_loss_ticks_large"] = GetInt(strategy, "strategy_b_stop_loss_ticks_large", 2);
                config["momentum_stop_loss_seconds"] = GetDouble(strategy, "momentum_stop_loss_seconds", 15.0);
                config["momentum_stop_loss_seconds_extended"] = GetDouble(strategy, "momentum_stop_loss_seconds_extended", 30.0);
                config["momentum_stop_loss_amount"] = GetDouble(strategy, "momentum_stop_loss_amount", 5000000.0);
                config["momentum_extended_capital_threshold"] = GetDouble(strategy, "momentum_extended_capital_threshold", 50000000000);
                config["momentum_extended_price_min"] = GetDouble(strategy, "momentum_extended_price_min", 100.0);
                config["momentum_extended_price_max"] = GetDouble(strategy, "momentum_extended_price_max", 500.0);

                // Trailing stop configuration
                config["trailing_stop"] = ParseTrailingStopConfig(strategy);

                // Strategy mode and general settings
                config["strategy_mode"] = GetString(strategy, "strategy_mode", "B");
                config["grace_period"] = GetDouble(strategy, "grace_period", 3.0);
                config["price_stop_loss_ticks"] = GetInt(strategy, "price_stop_loss_ticks", 3);
                config["exit_mode"] = GetString(strategy, "exit_mode", "day_high");
                config["day_high_drawdown_threshold"] = GetDouble(strategy, "day_high_drawdown_threshold", 0.0086);
                config["strategy_b_trailing_stop_minutes"] = GetInt(strategy, "strategy_b_trailing_stop_minutes", 3);
                config["strategy_b_second_stage_minutes"] = GetInt(strategy, "strategy_b_second_stage_minutes", 5);

                // Exhaustion parameters (V6.1/V7/V7.1)
                config["exhaustion_enabled"] = GetBool(strategy, "exhaustion_enabled", false);
                config["exhaustion_growth_rate_threshold"] = GetDouble(strategy, "exhaustion_growth_rate_threshold", 0.0086);
                config["exhaustion_buy_weak_streak"] = GetInt(strategy, "exhaustion_buy_weak_streak", 5);
                config["exhaustion_growth_drawdown"] = GetDouble(strategy, "exhaustion_growth_drawdown", 0.005);
                config["peak_tracking_delay"] = GetDouble(strategy, "peak_tracking_delay", 3.0);
                config["exhaustion_drawdown"] = GetDouble(strategy, "exhaustion_drawdown", 0.20);
                config["exhaustion_price_drawdown_ticks"] = GetInt(strategy, "exhaustion_price_drawdown_ticks", 2);
                config["exhaustion_time_rounding_enabled"] = GetBool(strategy, "exhaustion_time_rounding_enabled", true);
                config["exhaustion_breakout_reset_enabled"] = GetBool(strategy, "exhaustion_breakout_reset_enabled", true);
                config["exhaustion_breakout_ratio_threshold"] = GetDouble(strategy, "exhaustion_breakout_ratio_threshold", 5.5);
                config["exhaustion_breakout_price_new_high"] = GetBool(strategy, "exhaustion_breakout_price_new_high", true);
                config["exhaustion_require_ratio_exceed_peak"] = GetBool(strategy, "exhaustion_require_ratio_exceed_peak", true);
                config["exhaustion_require_surge"] = GetBool(strategy, "exhaustion_require_surge", true);
                config["exhaustion_surge_threshold"] = GetDouble(strategy, "exhaustion_surge_threshold", 1.4);

                // Exit sampling
                config["exit_sampling_enabled"] = GetBool(strategy, "exit_sampling_enabled", false);
                config["exit_sampling_interval"] = GetDouble(strategy, "exit_sampling_interval", 1.0);
                config["exit_sampling_price_type"] = GetString(strategy, "exit_sampling_price_type", "last");

                // Double peak
                config["double_peak_enabled"] = GetBool(strategy, "double_peak_enabled", false);
                config["double_peak_multiplier"] = GetDouble(strategy, "double_peak_multiplier", 1.5);
                config["double_peak_max_price_diff_pct"] = GetDouble(strategy, "double_peak_max_price_diff_pct", 0.8);
                config["double_peak_min_interval_minutes"] = GetInt(strategy, "double_peak_min_interval_minutes", 20);

                // Feature calculation
                config["calculate_low_15m_enabled"] = GetBool(strategy, "calculate_low_15m_enabled", false);

                // Stop-loss limit
                config["stop_loss_limit_enabled"] = GetBool(strategy, "stop_loss_limit_enabled", false);
                config["max_stop_loss_count"] = GetInt(strategy, "max_stop_loss_count", 2);
                config["stop_loss_reset_on_win"] = GetBool(strategy, "stop_loss_reset_on_win", false);

                // VWAP deviation exit
                config["vwap_deviation_exit_enabled"] = GetBool(strategy, "vwap_deviation_exit_enabled", false);
                config["vwap_deviation_threshold"] = GetDouble(strategy, "vwap_deviation_threshold", 2.0);
                config["vwap_column"] = GetString(strategy, "vwap_column", "vwap");

                // High 3-min drawdown exit
                config["high_3min_drawdown_enabled"] = GetBool(strategy, "high_3min_drawdown_enabled", true);
                config["high_3min_drawdown_threshold"] = GetDouble(strategy, "high_3min_drawdown_threshold", 0.025);

                // Observation signals (mark only, do not trigger exit)
                config["volume_shrink_signal_enabled"] = GetBool(strategy, "volume_shrink_signal_enabled", true);
                config["volume_shrink_lookback_seconds"] = GetInt(strategy, "volume_shrink_lookback_seconds", 5);
                config["volume_shrink_ratio"] = GetDouble(strategy, "volume_shrink_ratio", 5.0);
                config["vwap_deviation_signal_enabled"] = GetBool(strategy, "vwap_deviation_signal_enabled", false);

                // Ask wall exit (大壓單出場)
                config["ask_wall_exit_enabled"] = GetBool(strategy, "ask_wall_exit_enabled", true);
                config["ask_wall_dominance_ratio"] = GetDouble(strategy, "ask_wall_dominance_ratio", 3.0);
                config["ask_wall_min_amount_floor"] = GetDouble(strategy, "ask_wall_min_amount_floor", 1000000.0);
                config["ask_wall_bid_ask_ratio"] = GetDouble(strategy, "ask_wall_bid_ask_ratio", 2.0);
                config["ask_wall_vwap_deviation"] = GetDouble(strategy, "ask_wall_vwap_deviation", 1.8);
                config["ask_wall_confirm_seconds"] = GetDouble(strategy, "ask_wall_confirm_seconds", 15.0);
                config["ask_wall_exit_ratio"] = GetDouble(strategy, "ask_wall_exit_ratio", 0.333);

                // Mode C 掛單停利 (exit_mode_c)
                config["exit_mode_c"] = ParseModeCConfig(strategy);

                // Mode D 百分比停損 + 分鐘低點分批停利 (exit_mode_d)
                config["exit_mode_d"] = ParseModeDConfig(strategy);

                // Mode E 百分比停利 + 15分鐘低點安全網 (exit_mode_e)
                config["exit_mode_e"] = ParseModeEConfig(strategy);

                // Split entry (分批進場 Lot B)
                config["split_entry"] = ParseSplitEntryConfig(strategy);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[WARNING] Failed to load config: {ex.Message}, using defaults.");
            }

            return config;
        }

        private Dictionary<string, object> ParseTrailingStopConfig(Dictionary<string, object> strategy)
        {
            var result = new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["levels"] = new List<Dictionary<string, object>>(),
                ["entry_price_protection"] = true
            };

            if (!strategy.ContainsKey("trailing_stop")) return result;

            if (strategy["trailing_stop"] is Dictionary<object, object> tsRaw)
            {
                var ts = new Dictionary<string, object>();
                foreach (var kv in tsRaw)
                    ts[kv.Key?.ToString() ?? ""] = kv.Value;

                result["enabled"] = GetBool(ts, "enabled", true);
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
                ["massive_matching_amount"] = 50000000.0,  // 50M to match Python Bo_v2.yaml (5_0000_000)
                ["massive_matching_window"] = 1,  // seconds, default 1s
                ["use_dynamic_liquidity_threshold"] = false,  // Match Python: default off
                ["dynamic_liquidity_threshold_file"] = "daily_liquidity_threshold.parquet",
                ["dynamic_liquidity_multiplier"] = 0.004,  // Match Python: 0.4% of base amount
                ["dynamic_liquidity_threshold_cap"] = 50_000_000.0,
                ["ratio_entry_enabled"] = true,
                ["ratio_entry_threshold"] = 3.0,
                ["ratio_skip_before_time"] = "",
                ["ratio_column"] = "ratio_15s_300s",
                ["price_change_limit_enabled"] = true,
                ["price_change_limit_pct"] = 8.5,
                ["interval_pct_filter_enabled"] = true,  // Match Python: true
                ["interval_pct_minutes"] = 5,  // Match Python: 5
                ["interval_pct_threshold"] = 3.0,  // Match Python: 3.0
                ["entry_buffer_enabled"] = true,  // Must be true to allow entry after breakout
                ["entry_buffer_milliseconds"] = 100,  // Match Python: 100ms
                ["above_open_check_enabled"] = false,
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
                ["time_settings"] = "3s",
                ["output_path"] = @"D:/回測結果",
                ["ratio_increase_after_loss_enabled"] = true,
                ["ratio_increase_multiplier"] = 0.8,
                ["ratio_increase_min_threshold"] = 6.0,
                ["strategy_b_stop_loss_ticks_small"] = 3,
                ["strategy_b_stop_loss_ticks_large"] = 2,
                ["momentum_stop_loss_seconds"] = 15.0,
                ["momentum_stop_loss_seconds_extended"] = 30.0,
                ["momentum_stop_loss_amount"] = 5000000.0,
                ["momentum_extended_capital_threshold"] = 50000000000.0,
                ["momentum_extended_price_min"] = 100.0,
                ["momentum_extended_price_max"] = 500.0,
                ["trailing_stop"] = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["levels"] = new List<Dictionary<string, object>>(),
                    ["entry_price_protection"] = true
                },
                // Strategy mode and general settings (from Bo_v2.yaml)
                ["strategy_mode"] = "B",
                ["grace_period"] = 3.0,
                ["price_stop_loss_ticks"] = 3,
                ["exit_mode"] = "day_high",
                ["day_high_drawdown_threshold"] = 0.0086,
                ["strategy_b_trailing_stop_minutes"] = 3,
                ["strategy_b_second_stage_minutes"] = 5,
                // Exhaustion parameters (V6.1/V7/V7.1)
                ["exhaustion_enabled"] = false,
                ["exhaustion_growth_rate_threshold"] = 0.0086,
                ["exhaustion_buy_weak_streak"] = 5,
                ["exhaustion_growth_drawdown"] = 0.005,
                ["peak_tracking_delay"] = 3.0,
                ["exhaustion_drawdown"] = 0.20,
                ["exhaustion_price_drawdown_ticks"] = 2,
                ["exhaustion_time_rounding_enabled"] = true,
                ["exhaustion_breakout_reset_enabled"] = true,
                ["exhaustion_breakout_ratio_threshold"] = 5.5,
                ["exhaustion_breakout_price_new_high"] = true,
                ["exhaustion_require_ratio_exceed_peak"] = true,
                ["exhaustion_require_surge"] = true,
                ["exhaustion_surge_threshold"] = 1.4,
                // Price change limit
                ["price_change_limit_enabled"] = true,
                ["price_change_limit_pct_default"] = 8.5,
                // Min price change filter
                ["min_price_change_enabled"] = false,
                ["min_price_change_pct"] = 0.0,
                // MA5 bias threshold
                ["ma5_bias5_enabled"] = false,
                ["ma5_bias5_file"] = "ma5_bias5_threshold.parquet",
                // Low ratio filter
                ["low_ratio_filter_enabled"] = false,
                ["low_ratio_threshold"] = 1.005,
                // Exit sampling
                ["exit_sampling_enabled"] = false,
                ["exit_sampling_interval"] = 1.0,
                ["exit_sampling_price_type"] = "last",
                // Double peak
                ["double_peak_enabled"] = false,
                ["double_peak_multiplier"] = 1.5,
                ["double_peak_max_price_diff_pct"] = 0.8,
                ["double_peak_min_interval_minutes"] = 20,
                // Feature calculation
                ["calculate_low_15m_enabled"] = false,
                // Stop-loss limit
                ["stop_loss_limit_enabled"] = false,
                ["max_stop_loss_count"] = 2,
                ["stop_loss_reset_on_win"] = false,
                // VWAP deviation exit
                ["vwap_deviation_exit_enabled"] = false,
                ["vwap_deviation_threshold"] = 2.0,  // 2.0% to match Python Bo_v2.yaml
                ["vwap_column"] = "vwap",
                // High 3-min drawdown exit
                ["high_3min_drawdown_enabled"] = true,
                ["high_3min_drawdown_threshold"] = 0.025,  // 2.5% to match Python Bo_v2.yaml
                // Observation signals (mark only, do not trigger exit)
                ["volume_shrink_signal_enabled"] = true,
                ["volume_shrink_lookback_seconds"] = 5,
                ["volume_shrink_ratio"] = 5.0,
                ["vwap_deviation_signal_enabled"] = false,
                // Mode C 掛單停利
                ["exit_mode_c"] = new Dictionary<string, object>
                {
                    ["enabled"] = false,
                    ["vwap_5m_deviation_pct"] = 0.3,
                    ["vwap_5m_column"] = "vwap_5m",
                    ["stage1_exit_ratio"] = 0.333,
                    ["stage2_exit_ratio"] = 0.333,
                    ["stage3_exit_ratio"] = 0.334,
                    ["tighten_stop_after_stage1"] = false
                },
                // Mode D 百分比停損 + 分鐘低點分批停利
                ["exit_mode_d"] = new Dictionary<string, object>
                {
                    ["enabled"] = false,
                    ["stop_loss_pct"] = 1.2,
                    ["stage1_field"] = "low_1m",
                    ["stage2_field"] = "low_3m",
                    ["stage3_field"] = "low_5m",
                    ["stage1_exit_ratio"] = 0.333,
                    ["stage2_exit_ratio"] = 0.333,
                    ["stage3_exit_ratio"] = 0.334
                },
                // Mode E 百分比停利 + 15分鐘低點安全網
                ["exit_mode_e"] = new Dictionary<string, object>
                {
                    ["enabled"] = false,
                    ["stop_loss_pct"] = 1.2,
                    ["target1_pct"] = 0.8,
                    ["target2_pct"] = 1.2,
                    ["target3_pct"] = 1.6,
                    ["target1_exit_ratio"] = 0.333,
                    ["target2_exit_ratio"] = 0.333,
                    ["target3_exit_ratio"] = 0.334,
                    ["safety_net_field"] = "low_15m"
                },
                // Split entry (分批進場 Lot B)
                ["split_entry"] = new Dictionary<string, object>
                {
                    ["enabled"] = false,
                    ["lot_b_tick_offset"] = -1,
                    ["lot_b_timer_seconds"] = 10.0,
                    ["lot_b_exit_reference"] = "lot_a"
                },
                // Ask wall exit (大壓單出場)
                ["ask_wall_exit_enabled"] = true,
                ["ask_wall_dominance_ratio"] = 3.0,
                ["ask_wall_min_amount_floor"] = 1000000.0,
                ["ask_wall_bid_ask_ratio"] = 2.0,
                ["ask_wall_vwap_deviation"] = 1.8,
                ["ask_wall_confirm_seconds"] = 15.0,
                ["ask_wall_exit_ratio"] = 0.333
            };
        }

        private Dictionary<string, object> ParseModeCConfig(Dictionary<string, object> strategy)
        {
            var result = new Dictionary<string, object>
            {
                ["enabled"] = false,
                ["vwap_5m_deviation_pct"] = 0.3,
                ["vwap_5m_column"] = "vwap_5m",
                ["stage1_exit_ratio"] = 0.333,
                ["stage2_exit_ratio"] = 0.333,
                ["stage3_exit_ratio"] = 0.334,
                ["tighten_stop_after_stage1"] = false
            };

            if (!strategy.ContainsKey("exit_mode_c")) return result;

            if (strategy["exit_mode_c"] is Dictionary<object, object> mcRaw)
            {
                var mc = new Dictionary<string, object>();
                foreach (var kv in mcRaw)
                    mc[kv.Key?.ToString() ?? ""] = kv.Value;

                result["enabled"] = GetBool(mc, "enabled", false);
                result["vwap_5m_deviation_pct"] = GetDouble(mc, "vwap_5m_deviation_pct", 0.3);
                result["vwap_5m_column"] = GetString(mc, "vwap_5m_column", "vwap_5m");
                result["stage1_exit_ratio"] = GetDouble(mc, "stage1_exit_ratio", 0.333);
                result["stage2_exit_ratio"] = GetDouble(mc, "stage2_exit_ratio", 0.333);
                result["stage3_exit_ratio"] = GetDouble(mc, "stage3_exit_ratio", 0.334);
                result["tighten_stop_after_stage1"] = GetBool(mc, "tighten_stop_after_stage1", false);
            }

            return result;
        }

        private Dictionary<string, object> ParseModeDConfig(Dictionary<string, object> strategy)
        {
            var result = new Dictionary<string, object>
            {
                ["enabled"] = false,
                ["stop_loss_pct"] = 1.2,
                ["stage1_field"] = "low_1m",
                ["stage2_field"] = "low_3m",
                ["stage3_field"] = "low_5m",
                ["stage1_exit_ratio"] = 0.333,
                ["stage2_exit_ratio"] = 0.333,
                ["stage3_exit_ratio"] = 0.334
            };

            if (!strategy.ContainsKey("exit_mode_d")) return result;

            if (strategy["exit_mode_d"] is Dictionary<object, object> mdRaw)
            {
                var md = new Dictionary<string, object>();
                foreach (var kv in mdRaw)
                    md[kv.Key?.ToString() ?? ""] = kv.Value;

                result["enabled"] = GetBool(md, "enabled", false);
                result["stop_loss_pct"] = GetDouble(md, "stop_loss_pct", 1.2);
                result["stage1_field"] = GetString(md, "stage1_field", "low_1m");
                result["stage2_field"] = GetString(md, "stage2_field", "low_3m");
                result["stage3_field"] = GetString(md, "stage3_field", "low_5m");
                result["stage1_exit_ratio"] = GetDouble(md, "stage1_exit_ratio", 0.333);
                result["stage2_exit_ratio"] = GetDouble(md, "stage2_exit_ratio", 0.333);
                result["stage3_exit_ratio"] = GetDouble(md, "stage3_exit_ratio", 0.334);
            }

            return result;
        }

        private Dictionary<string, object> ParseModeEConfig(Dictionary<string, object> strategy)
        {
            var result = new Dictionary<string, object>
            {
                ["enabled"] = false,
                ["stop_loss_pct"] = 1.2,
                ["target1_pct"] = 0.8,
                ["target2_pct"] = 1.2,
                ["target3_pct"] = 1.6,
                ["target1_exit_ratio"] = 0.333,
                ["target2_exit_ratio"] = 0.333,
                ["target3_exit_ratio"] = 0.334,
                ["safety_net_field"] = "low_15m"
            };

            if (!strategy.ContainsKey("exit_mode_e")) return result;

            if (strategy["exit_mode_e"] is Dictionary<object, object> meRaw)
            {
                var me = new Dictionary<string, object>();
                foreach (var kv in meRaw)
                    me[kv.Key?.ToString() ?? ""] = kv.Value;

                result["enabled"] = GetBool(me, "enabled", false);
                result["stop_loss_pct"] = GetDouble(me, "stop_loss_pct", 1.2);
                result["target1_pct"] = GetDouble(me, "target1_pct", 0.8);
                result["target2_pct"] = GetDouble(me, "target2_pct", 1.2);
                result["target3_pct"] = GetDouble(me, "target3_pct", 1.6);
                result["target1_exit_ratio"] = GetDouble(me, "target1_exit_ratio", 0.333);
                result["target2_exit_ratio"] = GetDouble(me, "target2_exit_ratio", 0.333);
                result["target3_exit_ratio"] = GetDouble(me, "target3_exit_ratio", 0.334);
                result["safety_net_field"] = GetString(me, "safety_net_field", "low_15m");
            }

            return result;
        }

        private Dictionary<string, object> ParseSplitEntryConfig(Dictionary<string, object> strategy)
        {
            var result = new Dictionary<string, object>
            {
                ["enabled"] = false,
                ["lot_b_tick_offset"] = -1,
                ["lot_b_timer_seconds"] = 10.0,
                ["lot_b_exit_reference"] = "lot_a"
            };

            if (!strategy.ContainsKey("split_entry")) return result;

            if (strategy["split_entry"] is Dictionary<object, object> seRaw)
            {
                var se = new Dictionary<string, object>();
                foreach (var kv in seRaw)
                    se[kv.Key?.ToString() ?? ""] = kv.Value;

                result["enabled"] = GetBool(se, "enabled", false);
                result["lot_b_tick_offset"] = GetInt(se, "lot_b_tick_offset", -1);
                result["lot_b_timer_seconds"] = GetDouble(se, "lot_b_timer_seconds", 10.0);
                result["lot_b_exit_reference"] = GetString(se, "lot_b_exit_reference", "lot_a");
            }

            return result;
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
                "massive_matching_enabled", "massive_matching_amount", "massive_matching_window",
                "use_dynamic_liquidity_threshold", "dynamic_liquidity_threshold_file",
                "dynamic_liquidity_multiplier", "dynamic_liquidity_threshold_cap",
                "ratio_entry_enabled", "ratio_entry_threshold", "ratio_skip_before_time", "ratio_column",
                "price_change_limit_enabled", "price_change_limit_pct",
                "min_price_change_enabled", "min_price_change_pct",
                "ma5_bias5_enabled", "ma5_bias5_file",
                "low_ratio_filter_enabled", "low_ratio_threshold",
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
                "ratio_increase_min_threshold",
                "stop_loss_limit_enabled", "max_stop_loss_count", "stop_loss_reset_on_win",
                "split_entry"
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
                "trailing_stop",
                "strategy_mode", "grace_period", "price_stop_loss_ticks",
                "exit_mode", "day_high_drawdown_threshold",
                "strategy_b_trailing_stop_minutes", "strategy_b_second_stage_minutes",
                "exhaustion_enabled", "exhaustion_growth_rate_threshold",
                "exhaustion_buy_weak_streak", "exhaustion_growth_drawdown",
                "peak_tracking_delay", "exhaustion_drawdown", "exhaustion_price_drawdown_ticks",
                "exhaustion_time_rounding_enabled", "exhaustion_breakout_reset_enabled",
                "exhaustion_breakout_ratio_threshold", "exhaustion_breakout_price_new_high",
                "exhaustion_require_ratio_exceed_peak", "exhaustion_require_surge",
                "exhaustion_surge_threshold",
                "exit_sampling_enabled", "exit_sampling_interval", "exit_sampling_price_type",
                "double_peak_enabled", "double_peak_multiplier",
                "double_peak_max_price_diff_pct", "double_peak_min_interval_minutes",
                "stop_loss_limit_enabled", "max_stop_loss_count", "stop_loss_reset_on_win",
                "vwap_deviation_exit_enabled", "vwap_deviation_threshold", "vwap_column",
                "high_3min_drawdown_enabled", "high_3min_drawdown_threshold",
                "volume_shrink_signal_enabled", "volume_shrink_lookback_seconds", "volume_shrink_ratio",
                "vwap_deviation_signal_enabled",
                "ask_wall_exit_enabled", "ask_wall_dominance_ratio", "ask_wall_min_amount_floor",
                "ask_wall_bid_ask_ratio", "ask_wall_vwap_deviation",
                "ask_wall_confirm_seconds", "ask_wall_exit_ratio",
                "exit_mode_c",
                "exit_mode_d",
                "exit_mode_e"
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
