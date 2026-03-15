using System;
using System.Collections.Generic;
using System.Globalization;
using BacktestModule.Core.Models;

namespace BacktestModule.Strategy
{
    /// <summary>
    /// Strongly-typed exit result. Replaces Dictionary&lt;string, object&gt; to eliminate boxing/unboxing.
    /// Retains indexer for backward compatibility with code using ["key"] access pattern.
    /// </summary>
    public class ExitResult
    {
        public string ExitType { get; set; }
        public string ExitReason { get; set; }
        public double ExitPrice { get; set; }
        public DateTime ExitTime { get; set; }
        public double ExitRatio { get; set; } = 1.0;
        public string ExitLevel { get; set; }

        // Optional extra fields (avoid separate dictionary for rare metadata)
        public double? StopLossPrice { get; set; }
        public int? StopLossTicks { get; set; }
        public string TickType { get; set; }
        public double? GrowthRate { get; set; }
        public double? BalanceRatio { get; set; }
        public int? BuyWeakStreak { get; set; }
        public double? GrowthDrawdown { get; set; }
        public double? ExitThreshold { get; set; }
        public double? VwapValue { get; set; }
        public double? DeviationPct { get; set; }

        public ExitResult() { }

        public static ExitResult Create(
            string exitType, string exitReason, double exitPrice, DateTime exitTime,
            double exitRatio = 1.0, string exitLevel = null)
        {
            return new ExitResult
            {
                ExitType = exitType,
                ExitReason = exitReason,
                ExitPrice = exitPrice,
                ExitTime = exitTime,
                ExitRatio = exitRatio,
                ExitLevel = exitLevel
            };
        }

        /// <summary>
        /// Indexer for backward compatibility. Maps string keys to strongly-typed properties.
        /// </summary>
        public object this[string key]
        {
            get
            {
                return key switch
                {
                    "exit_type" => ExitType,
                    "exit_reason" => ExitReason,
                    "exit_price" => (object)ExitPrice,
                    "exit_time" => (object)ExitTime,
                    "exit_ratio" => (object)ExitRatio,
                    "exit_level" => ExitLevel,
                    "stop_loss_price" => (object)StopLossPrice,
                    "stop_loss_ticks" => (object)StopLossTicks,
                    "tick_type" => TickType,
                    "growth_rate" => (object)GrowthRate,
                    "balance_ratio" => (object)BalanceRatio,
                    "buy_weak_streak" => (object)BuyWeakStreak,
                    "growth_drawdown" => (object)GrowthDrawdown,
                    "exit_threshold" => (object)ExitThreshold,
                    "vwap_value" => (object)VwapValue,
                    "deviation_pct" => (object)DeviationPct,
                    _ => null
                };
            }
            set
            {
                switch (key)
                {
                    case "exit_type": ExitType = value?.ToString(); break;
                    case "exit_reason": ExitReason = value?.ToString(); break;
                    case "exit_price": ExitPrice = Convert.ToDouble(value); break;
                    case "exit_time": ExitTime = (DateTime)value; break;
                    case "exit_ratio": ExitRatio = Convert.ToDouble(value); break;
                    case "exit_level": ExitLevel = value?.ToString(); break;
                    case "stop_loss_price": StopLossPrice = Convert.ToDouble(value); break;
                    case "stop_loss_ticks": StopLossTicks = Convert.ToInt32(value); break;
                    case "tick_type": TickType = value?.ToString(); break;
                    case "growth_rate": GrowthRate = Convert.ToDouble(value); break;
                    case "balance_ratio": BalanceRatio = Convert.ToDouble(value); break;
                    case "buy_weak_streak": BuyWeakStreak = Convert.ToInt32(value); break;
                    case "growth_drawdown": GrowthDrawdown = Convert.ToDouble(value); break;
                    case "exit_threshold": ExitThreshold = Convert.ToDouble(value); break;
                    case "vwap_value": VwapValue = Convert.ToDouble(value); break;
                    case "deviation_pct": DeviationPct = Convert.ToDouble(value); break;
                }
            }
        }

        /// <summary>
        /// Compatibility method for Dictionary.GetValueOrDefault pattern.
        /// </summary>
        public object GetValueOrDefault(string key, object defaultValue = null)
        {
            var val = this[key];
            return val ?? defaultValue;
        }
    }

    /// <summary>
    /// Exit Manager: manages all exit conditions.
    /// </summary>
    public class ExitManager
    {
        private readonly Dictionary<string, object> _config;
        public bool TrailingStopEnabled { get; private set; }
        public List<Dictionary<string, object>> TrailingStopLevels { get; }
        public bool EntryPriceProtection { get; private set; }

        // Mode C properties
        public bool ModeCEnabled { get; }
        public double ModeCVwap5mDeviationPct { get; }
        public string ModeCVwap5mColumn { get; }
        public double ModeCStage1Ratio { get; }
        public double ModeCStage2Ratio { get; }
        public double ModeCStage3Ratio { get; }
        public bool ModeCTightenStopAfterStage1 { get; }

        // Mode D properties
        public bool ModeDEnabled { get; }
        public double ModeDStopLossPct { get; }
        public string ModeDStage1Field { get; }
        public string ModeDStage2Field { get; }
        public string ModeDStage3Field { get; }
        public double ModeDStage1Ratio { get; }
        public double ModeDStage2Ratio { get; }
        public double ModeDStage3Ratio { get; }

        // Mode E properties
        public bool ModeEEnabled { get; }
        public double ModeEStopLossPct { get; }
        public double ModeETarget1Pct { get; }
        public double ModeETarget2Pct { get; }
        public double ModeETarget3Pct { get; }
        public double ModeETarget1Ratio { get; }
        public double ModeETarget2Ratio { get; }
        public double ModeETarget3Ratio { get; }
        public string ModeESafetyNetField { get; }

        public ExitManager(Dictionary<string, object> config)
        {
            _config = config;

            // Read trailing stop configuration
            var trailingConfig = config.TryGetValue("trailing_stop", out var ts)
                ? ts as Dictionary<string, object> ?? new Dictionary<string, object>()
                : new Dictionary<string, object>();

            TrailingStopEnabled = GetBoolFrom(trailingConfig, "enabled", false);
            EntryPriceProtection = GetBoolFrom(trailingConfig, "entry_price_protection", true);

            TrailingStopLevels = new List<Dictionary<string, object>>();
            if (trailingConfig.TryGetValue("levels", out var levels) && levels is List<Dictionary<string, object>> levelList)
            {
                TrailingStopLevels = levelList;
            }

            // Mode C configuration
            var modeCConfig = config.TryGetValue("exit_mode_c", out var mc)
                ? mc as Dictionary<string, object> ?? new Dictionary<string, object>()
                : new Dictionary<string, object>();

            ModeCEnabled = GetBoolFrom(modeCConfig, "enabled", false);
            ModeCVwap5mDeviationPct = modeCConfig.TryGetValue("vwap_5m_deviation_pct", out var vdp)
                ? Convert.ToDouble(vdp) : 0.3;
            ModeCVwap5mColumn = modeCConfig.TryGetValue("vwap_5m_column", out var vc5)
                ? vc5?.ToString() ?? "vwap_5m" : "vwap_5m";
            ModeCStage1Ratio = modeCConfig.TryGetValue("stage1_exit_ratio", out var s1)
                ? Convert.ToDouble(s1) : 0.333;
            ModeCStage2Ratio = modeCConfig.TryGetValue("stage2_exit_ratio", out var s2)
                ? Convert.ToDouble(s2) : 0.333;
            ModeCStage3Ratio = modeCConfig.TryGetValue("stage3_exit_ratio", out var s3)
                ? Convert.ToDouble(s3) : 0.334;
            ModeCTightenStopAfterStage1 = GetBoolFrom(modeCConfig, "tighten_stop_after_stage1", false);

            // Mode C: auto-disable trailing_stop, force entry_price_protection
            if (ModeCEnabled)
            {
                TrailingStopEnabled = false;
                EntryPriceProtection = true;
            }

            // Mode D configuration
            var modeDConfig = config.TryGetValue("exit_mode_d", out var md)
                ? md as Dictionary<string, object> ?? new Dictionary<string, object>()
                : new Dictionary<string, object>();

            ModeDEnabled = GetBoolFrom(modeDConfig, "enabled", false);
            ModeDStopLossPct = modeDConfig.TryGetValue("stop_loss_pct", out var slp)
                ? Convert.ToDouble(slp) : 1.2;
            ModeDStage1Field = modeDConfig.TryGetValue("stage1_field", out var sf1)
                ? sf1?.ToString() ?? "low_1m" : "low_1m";
            ModeDStage2Field = modeDConfig.TryGetValue("stage2_field", out var sf2)
                ? sf2?.ToString() ?? "low_3m" : "low_3m";
            ModeDStage3Field = modeDConfig.TryGetValue("stage3_field", out var sf3)
                ? sf3?.ToString() ?? "low_5m" : "low_5m";
            ModeDStage1Ratio = modeDConfig.TryGetValue("stage1_exit_ratio", out var dr1)
                ? Convert.ToDouble(dr1) : 0.333;
            ModeDStage2Ratio = modeDConfig.TryGetValue("stage2_exit_ratio", out var dr2)
                ? Convert.ToDouble(dr2) : 0.333;
            ModeDStage3Ratio = modeDConfig.TryGetValue("stage3_exit_ratio", out var dr3)
                ? Convert.ToDouble(dr3) : 0.334;

            // Mode D: auto-disable trailing_stop, force entry_price_protection
            if (ModeDEnabled)
            {
                TrailingStopEnabled = false;
                EntryPriceProtection = true;
            }

            // Mode E configuration
            var modeEConfig = config.TryGetValue("exit_mode_e", out var me)
                ? me as Dictionary<string, object> ?? new Dictionary<string, object>()
                : new Dictionary<string, object>();

            ModeEEnabled = GetBoolFrom(modeEConfig, "enabled", false);
            ModeEStopLossPct = modeEConfig.TryGetValue("stop_loss_pct", out var eslp)
                ? Convert.ToDouble(eslp) : 1.2;
            ModeETarget1Pct = modeEConfig.TryGetValue("target1_pct", out var et1)
                ? Convert.ToDouble(et1) : 0.8;
            ModeETarget2Pct = modeEConfig.TryGetValue("target2_pct", out var et2)
                ? Convert.ToDouble(et2) : 1.2;
            ModeETarget3Pct = modeEConfig.TryGetValue("target3_pct", out var et3)
                ? Convert.ToDouble(et3) : 1.6;
            ModeETarget1Ratio = modeEConfig.TryGetValue("target1_exit_ratio", out var er1)
                ? Convert.ToDouble(er1) : 0.333;
            ModeETarget2Ratio = modeEConfig.TryGetValue("target2_exit_ratio", out var er2)
                ? Convert.ToDouble(er2) : 0.333;
            ModeETarget3Ratio = modeEConfig.TryGetValue("target3_exit_ratio", out var er3)
                ? Convert.ToDouble(er3) : 0.334;
            ModeESafetyNetField = modeEConfig.TryGetValue("safety_net_field", out var snf)
                ? snf?.ToString() ?? "low_15m" : "low_15m";

            // Mode E: auto-disable trailing_stop, force entry_price_protection
            if (ModeEEnabled)
            {
                TrailingStopEnabled = false;
                EntryPriceProtection = true;
            }
        }

        /// <summary>
        /// Checks hard stop-loss: Day High at entry - N ticks.
        /// Uses different tick counts for small vs large tick sizes.
        /// </summary>
        public ExitResult CheckHardStopLoss(Position position, double currentPrice, DateTime currentTime, int tickOffset = 0)
        {
            double entryTickSize = TickSizeHelper.GetTickSize(position.DayHighAtEntry);

            int stopLossTicks;
            string tickType;
            if (entryTickSize == 0.5 || entryTickSize == 5.0)
            {
                stopLossTicks = GetInt("strategy_b_stop_loss_ticks_large", 2);
                tickType = "large";
            }
            else
            {
                stopLossTicks = GetInt("strategy_b_stop_loss_ticks_small", 3);
                tickType = "small";
            }

            // Apply tick offset (e.g., -1 to tighten stop loss after Mode C Stage 1)
            stopLossTicks = Math.Max(1, stopLossTicks + tickOffset);

            double stopLossPrice = TickSizeHelper.AddTicks(position.DayHighAtEntry, -stopLossTicks);

            if (currentPrice <= stopLossPrice)
            {
                string tightenNote = tickOffset != 0 ? $", offset={tickOffset:+#;-#;0}" : "";
                System.Console.WriteLine(
                    $"[HARD STOP] Time: {currentTime}, Price: {currentPrice:F2}, " +
                    $"StopPrice: {stopLossPrice:F2} (DayHigh={position.DayHighAtEntry:F2} " +
                    $"down {stopLossTicks} ticks, tickType={tickType}{tightenNote})");

                var result = ExitResult.Create("remaining", "tick_stop_loss", currentPrice, currentTime);
                result.StopLossPrice = stopLossPrice;
                result.StopLossTicks = stopLossTicks;
                result.TickType = tickType;
                return result;
            }

            return null;
        }

        /// <summary>
        /// Checks limit-up exit: if price >= limitUpPrice, partial exit 50%.
        /// </summary>
        public ExitResult CheckLimitUpExit(Position position, double currentPrice, DateTime currentTime, double limitUpPrice)
        {
            if (currentPrice >= limitUpPrice)
            {
                System.Console.WriteLine($"[LIMIT UP EXIT] Time: {currentTime}, Price: {currentPrice:F2}");
                return ExitResult.Create("partial", "Limit-up exit", limitUpPrice, currentTime, 0.5);
            }
            return null;
        }

        /// <summary>
        /// Checks momentum exhaustion exit (Strategy A, first stage: 50% reduction).
        /// Requires >= 60s holding. Checks growth rate and buy weakness streak.
        /// </summary>
        public ExitResult CheckMomentumExhaustion(
            Position position,
            TickData row,
            DayHighMomentumTracker momentumTracker,
            OrderBookBalanceMonitor orderbookMonitor,
            DateTime currentTime,
            double currentPrice)
        {
            if (position.PartialExitDone) return null;

            double holdingSeconds = (currentTime - position.EntryTime).TotalSeconds;
            if (holdingSeconds < 60) return null;

            double growthRate = momentumTracker.GetGrowthRate();
            bool momentumSlowing = growthRate < 0.0086;

            double balanceRatio = orderbookMonitor.CalculateBalanceRatio(row);
            bool buyWeak = balanceRatio < 1.0;
            position.BuyWeakStreak = buyWeak ? position.BuyWeakStreak + 1 : 0;
            bool buyWeak5ticks = position.BuyWeakStreak >= 5;

            double growthDrawdown = Math.Max(momentumTracker.PeakGrowthRate - growthRate, 0.0);
            bool growthDrawdownTrigger = growthDrawdown >= 0.005;

            bool shouldExit = (growthDrawdownTrigger && buyWeak5ticks) || momentumSlowing;

            if (shouldExit)
            {
                var reasons = new List<string>();
                if (growthDrawdownTrigger && buyWeak5ticks)
                    reasons.Add("Growth peak drawdown + weak order book (5 ticks)");
                if (momentumSlowing)
                    reasons.Add("Growth rate < 0.86%");
                string reasonText = string.Join(" / ", reasons);

                System.Console.WriteLine(
                    $"[MOMENTUM EXIT] Time: {currentTime}, Price: {currentPrice:F2}, " +
                    $"GrowthRate: {growthRate:P2}, Balance: {balanceRatio:F2}, Reason: {reasonText}");

                var result = ExitResult.Create("partial", $"Momentum exhaustion ({reasonText})", currentPrice, currentTime, 0.5);
                result.GrowthRate = growthRate;
                result.BalanceRatio = balanceRatio;
                result.BuyWeakStreak = position.BuyWeakStreak;
                result.GrowthDrawdown = growthDrawdown;
                return result;
            }

            return null;
        }

        /// <summary>
        /// Checks first stage partial exit (combines hard stop + limit up + momentum exhaustion).
        /// </summary>
        public ExitResult CheckPartialExit(
            Position position, TickData row,
            DayHighMomentumTracker momentumTracker,
            OrderBookBalanceMonitor orderbookMonitor,
            double limitUpPrice)
        {
            DateTime currentTime = row.Time;
            double currentPrice = row.Price;

            if (currentPrice > position.HighestPrice)
                position.HighestPrice = currentPrice;

            if (position.PartialExitDone) return null;

            var stopResult = CheckHardStopLoss(position, currentPrice, currentTime);
            if (stopResult != null) return stopResult;

            var limitResult = CheckLimitUpExit(position, currentPrice, currentTime, limitUpPrice);
            if (limitResult != null) return limitResult;

            var momResult = CheckMomentumExhaustion(position, row, momentumTracker, orderbookMonitor, currentTime, currentPrice);
            if (momResult != null) return momResult;

            return null;
        }

        /// <summary>
        /// Checks second stage exit: after partial exit, checks 3m low / entry price.
        /// </summary>
        public ExitResult CheckFinalExit(Position position, TickData row, DateTime currentTime, double currentPrice)
        {
            if (!position.PartialExitDone) return null;

            double low3m = row.Low3m;
            bool low3mValid = low3m > 0 && !double.IsNaN(low3m);
            double entryPrice = position.EntryPrice;
            bool entryValid = entryPrice > 0;

            double exitThreshold;
            string reasonText;

            if (low3mValid && entryValid && low3m > entryPrice)
            {
                exitThreshold = low3m;
                reasonText = $"Below 3m low {low3m:F2}";
            }
            else if (entryValid)
            {
                exitThreshold = entryPrice;
                reasonText = "Back to entry price";
            }
            else
            {
                return null;
            }

            if (currentPrice <= exitThreshold)
            {
                System.Console.WriteLine($"[FINAL EXIT] Time: {currentTime}, Price: {currentPrice:F2}, {reasonText}");
                var result = ExitResult.Create("remaining", $"Final exit ({reasonText})", currentPrice, currentTime);
                result.ExitThreshold = exitThreshold;
                return result;
            }

            return null;
        }

        /// <summary>
        /// Checks reentry stop-loss conditions.
        /// </summary>
        public ExitResult CheckReentryStopLoss(
            Position position, TickData row,
            DayHighMomentumTracker momentumTracker,
            OrderBookBalanceMonitor orderbookMonitor)
        {
            if (!position.PartialExitRecovered) return null;

            double currentPrice = row.Price;
            DateTime currentTime = row.Time;

            // 1. Price stop-loss
            if (position.ReentryStopPrice.HasValue && currentPrice <= position.ReentryStopPrice.Value)
            {
                System.Console.WriteLine(
                    $"[REENTRY STOP] Time: {currentTime}, Price: {currentPrice:F2}, " +
                    $"StopPrice: {position.ReentryStopPrice:F2}");
                return ExitResult.Create("reentry_stop", "Reentry stop-loss", currentPrice, currentTime);
            }

            // 2. Momentum exhaustion after 30s
            if (position.ReentryTime.HasValue)
            {
                double reentryElapsed = (currentTime - position.ReentryTime.Value).TotalSeconds;
                if (reentryElapsed >= 30.0)
                {
                    double growthRate = momentumTracker.GetGrowthRate();
                    double balanceRatio = orderbookMonitor.CalculateBalanceRatio(row);
                    double growthDrawdown = momentumTracker.GetGrowthDrawdown();

                    bool buyWeak = balanceRatio < 1.0;
                    position.BuyWeakStreak = buyWeak ? position.BuyWeakStreak + 1 : 0;

                    bool conditionA = growthRate < 0.0086;
                    bool conditionB = growthDrawdown >= 0.005 && position.BuyWeakStreak >= 5;

                    if (conditionA || conditionB)
                    {
                        var reasons = new List<string>();
                        if (conditionA) reasons.Add("Growth rate < 0.86%");
                        if (conditionB) reasons.Add("Drawdown >= 0.5% with weak order book");

                        position.ReentryMomentumStopTriggered = true;
                        position.ReentryMomentumStopTime = currentTime;

                        return ExitResult.Create("reentry_stop",
                            $"Reentry momentum exhaustion ({string.Join(" & ", reasons)})",
                            currentPrice, currentTime);
                    }
                }
            }

            // 3. After momentum stop, if price falls below reentry price
            if (position.ReentryMomentumStopTriggered && position.ReentryPrice.HasValue)
            {
                if (currentPrice < position.ReentryPrice.Value)
                {
                    return ExitResult.Create("reentry_stop",
                        "Momentum stop then below reentry price", currentPrice, currentTime);
                }
            }

            return null;
        }

        /// <summary>
        /// Checks trailing stop: each level (1min, 3min, 5min low).
        /// </summary>
        public ExitResult CheckTrailingStop(
            Position position, TickData row, DateTime currentTime, double currentPrice)
        {
            if (!TrailingStopEnabled) return null;
            if (position.RemainingRatio <= 0.1) return null;

            foreach (var levelConfig in TrailingStopLevels)
            {
                string levelName = levelConfig.TryGetValue("name", out var n) ? n.ToString() : "";
                string fieldName = levelConfig.TryGetValue("field", out var f) ? f.ToString() : "";
                double exitRatio = levelConfig.TryGetValue("exit_ratio", out var er)
                    ? Convert.ToDouble(er) : 0.333;

                // Skip already-triggered levels
                if (position.ExitLevelsTriggered.TryGetValue(levelName, out bool triggered) && triggered)
                    continue;

                // Get the low price from the tick data
                double lowPrice = row.GetFieldByName(fieldName);
                if (lowPrice <= 0 || double.IsNaN(lowPrice))
                    continue;

                if (currentPrice <= lowPrice)
                {
                    System.Console.WriteLine(
                        $"[TRAILING STOP] Time: {currentTime}, Price: {currentPrice:F2}, " +
                        $"Level: {levelName}, Low: {lowPrice:F2}");

                    return ExitResult.Create("trailing_stop",
                        $"Hit {levelName} low ({lowPrice:F2})",
                        currentPrice, currentTime, exitRatio, levelName);
                }
            }

            return null;
        }

        /// <summary>
        /// Checks entry price protection: after partial exit, if price <= entry price, exit all remaining.
        /// </summary>
        public ExitResult CheckEntryPriceProtection(
            Position position, double currentPrice, DateTime currentTime)
        {
            if (!EntryPriceProtection) return null;
            if (position.RemainingRatio >= 1.0) return null;
            if (position.RemainingRatio <= 0) return null;

            if (currentPrice <= position.EntryPrice)
            {
                System.Console.WriteLine(
                    $"[ENTRY PRICE PROTECTION] Time: {currentTime}, Price: {currentPrice:F2}, " +
                    $"EntryPrice: {position.EntryPrice:F2}, Remaining: {position.RemainingRatio:P1}");

                var result = ExitResult.Create("protection",
                    $"Entry price protection ({position.EntryPrice:F2})",
                    currentPrice, currentTime, position.RemainingRatio);
                return result;
            }

            return null;
        }

        /// <summary>
        /// Checks VWAP deviation exit: if price-to-VWAP positive deviation exceeds threshold, full exit.
        /// Mirrors Python: check_vwap_deviation_exit()
        /// </summary>
        public ExitResult CheckVwapDeviationExit(
            Position position, double currentPrice, DateTime currentTime, double vwap)
        {
            bool enabled = GetBoolConfig("vwap_deviation_exit_enabled", false);
            if (!enabled) return null;
            if (vwap <= 0 || double.IsNaN(vwap)) return null;
            if (currentPrice <= 0) return null;

            double threshold = GetDoubleConfig("vwap_deviation_threshold", 2.0);  // percentage (%)
            double deviationPct = (currentPrice - vwap) / vwap * 100.0;

            if (deviationPct >= threshold)
            {
                System.Console.WriteLine(
                    $"[VWAP EXIT] Time: {currentTime:HH:mm:ss.fff}, Price: {currentPrice:F2}, " +
                    $"VWAP: {vwap:F2}, Deviation: {deviationPct:F2}%, Threshold: {threshold:F2}%");

                var result = ExitResult.Create("remaining",
                    $"VWAP乖離出場(乖離{deviationPct:F2}%>{threshold:F1}%)",
                    currentPrice, currentTime, position.RemainingRatio);
                result.VwapValue = vwap;
                result.DeviationPct = deviationPct;
                return result;
            }

            return null;
        }

        /// <summary>
        /// Checks high 3-min drawdown exit.
        /// </summary>
        public ExitResult CheckHigh3MinDrawdownExit(
            Position position, double currentPrice, DateTime currentTime, double high3min)
        {
            bool enabled = GetBoolConfig("high_3min_drawdown_enabled", true);
            if (!enabled) return null;
            if (high3min <= 0 || double.IsNaN(high3min)) return null;

            double threshold = GetDoubleConfig("high_3min_drawdown_threshold", 0.025);
            double drawdown = (high3min - currentPrice) / high3min;

            if (drawdown >= threshold)
            {
                return ExitResult.Create("remaining",
                    $"High 3-min drawdown exit (dd={drawdown:P2})",
                    currentPrice, currentTime, position.RemainingRatio);
            }

            return null;
        }

        /// <summary>
        /// Checks ask wall signal (3 AND conditions).
        /// Only detects the signal; observation/confirmation is handled by BacktestLoop.
        /// Mirrors Python: check_ask_wall_signal()
        /// </summary>
        public bool CheckAskWallSignal(TickData row, DateTime currentTime, double currentPrice, double massiveThreshold)
        {
            if (!GetBoolConfig("ask_wall_exit_enabled", false))
                return false;

            // Condition 1: One ask level has abnormally large volume
            double[] askVolumes = {
                row.Ask1Volume, row.Ask2Volume, row.Ask3Volume,
                row.Ask4Volume, row.Ask5Volume
            };

            var positiveVols = new List<double>();
            foreach (var v in askVolumes)
            {
                if (v > 0 && !double.IsNaN(v))
                    positiveVols.Add(v);
            }
            if (positiveVols.Count < 2)
                return false;

            double maxVol = 0, minVol = double.MaxValue;
            foreach (var v in positiveVols)
            {
                if (v > maxVol) maxVol = v;
                if (v < minVol) minVol = v;
            }

            // 1a: max / min >= dominance_ratio
            double dominanceRatio = GetDoubleConfig("ask_wall_dominance_ratio", 3.0);
            if (minVol <= 0 || maxVol / minVol < dominanceRatio)
                return false;

            // 1b: max volume amount >= max(massive_threshold / 2, floor)
            double maxVolAmount = maxVol * currentPrice * 1000;
            double floorAmount = GetDoubleConfig("ask_wall_min_amount_floor", 1000000.0);
            double minAmountThreshold = Math.Max(massiveThreshold / 2.0, floorAmount);

            if (maxVolAmount < minAmountThreshold)
                return false;

            // Condition 2: bid_ask_ratio >= threshold
            double bidAskRatio = row.BidAskRatio;
            if (double.IsNaN(bidAskRatio) || bidAskRatio <= 0)
                return false;
            double bidAskThreshold = GetDoubleConfig("ask_wall_bid_ask_ratio", 2.0);
            if (bidAskRatio < bidAskThreshold)
                return false;

            // Condition 3: VWAP deviation >= threshold
            string vwapColumn = _config.TryGetValue("vwap_column", out var vc) ? vc?.ToString() ?? "vwap" : "vwap";
            double vwap = row.GetFieldByName(vwapColumn);
            if (vwap <= 0 || currentPrice <= 0)
                return false;
            double deviationPct = (currentPrice - vwap) / vwap * 100.0;
            double vwapThreshold = GetDoubleConfig("ask_wall_vwap_deviation", 1.8);
            if (deviationPct < vwapThreshold)
                return false;

            // Find max volume level for logging
            int maxIdx = Array.IndexOf(askVolumes, maxVol) + 1;
            System.Console.WriteLine(
                $"[大壓單訊號] 時間: {currentTime:HH:mm:ss.fff}, 價格: {currentPrice:F2}, " +
                $"委賣第{maxIdx}檔: {maxVol:F0}張 (最小檔: {minVol:F0}張, 比值: {maxVol / minVol:F1}x), " +
                $"最大檔金額: {maxVolAmount / 1e6:F2}M (門檻: {minAmountThreshold / 1e6:F2}M), " +
                $"bid_ask_ratio: {bidAskRatio:F2}, VWAP乖離: {deviationPct:F2}%");

            return true;
        }

        /// <summary>
        /// Mode C Stage 2: VWAP_5m negative deviation exit.
        /// Trigger: price &lt; vwap_5m × (1 - deviation_pct/100)
        /// Mirrors Python: check_vwap_5m_deviation_exit()
        /// </summary>
        public ExitResult CheckVwap5mDeviationExit(
            Position position, TickData row, DateTime currentTime, double currentPrice)
        {
            string vwapCol = ModeCVwap5mColumn;
            double vwapValue = row.GetFieldByName(vwapCol);

            if (vwapValue <= 0 || double.IsNaN(vwapValue) || currentPrice <= 0)
                return null;

            double thresholdPrice = vwapValue * (1 - ModeCVwap5mDeviationPct / 100.0);

            if (currentPrice < thresholdPrice)
            {
                double deviationPct = (currentPrice - vwapValue) / vwapValue * 100;
                System.Console.WriteLine(
                    $"[Mode C Stage2] Time: {currentTime:HH:mm:ss.fff}, Price: {currentPrice:F2}, " +
                    $"VWAP_5m: {vwapValue:F2}, Threshold: {thresholdPrice:F2}, " +
                    $"Deviation: {deviationPct:F2}%");

                return ExitResult.Create("trailing_stop",
                    $"Mode C VWAP_5m負向乖離({deviationPct:F2}%)",
                    currentPrice, currentTime, ModeCStage2Ratio, "mode_c_stage2");
            }
            return null;
        }

        /// <summary>
        /// Mode C Stage 1 fallback: break 1-minute low exit (when no ask wall).
        /// Trigger: price ≤ low_1m
        /// Mirrors Python: check_low_1m_exit()
        /// </summary>
        public ExitResult CheckLow1mExit(
            Position position, TickData row, DateTime currentTime, double currentPrice)
        {
            double low1m = row.Low1m;
            if (low1m <= 0 || double.IsNaN(low1m))
                return null;

            if (currentPrice <= low1m)
            {
                System.Console.WriteLine(
                    $"[Mode C Stage1 fallback] Time: {currentTime:HH:mm:ss.fff}, Price: {currentPrice:F2}, " +
                    $"low_1m: {low1m:F2}");

                return ExitResult.Create("trailing_stop",
                    $"Mode C 破1分鐘低點({low1m:F2})",
                    currentPrice, currentTime, ModeCStage1Ratio, "mode_c_stage1");
            }
            return null;
        }

        /// <summary>
        /// Mode C Stage 3: break 3-minute low exit.
        /// Trigger: price ≤ low_3m
        /// Mirrors Python: check_low_3m_exit()
        /// </summary>
        public ExitResult CheckLow3mExit(
            Position position, TickData row, DateTime currentTime, double currentPrice)
        {
            double low3m = row.Low3m;
            if (low3m <= 0 || double.IsNaN(low3m))
                return null;

            if (currentPrice <= low3m)
            {
                System.Console.WriteLine(
                    $"[Mode C Stage3] Time: {currentTime:HH:mm:ss.fff}, Price: {currentPrice:F2}, " +
                    $"low_3m: {low3m:F2}");

                return ExitResult.Create("trailing_stop",
                    $"Mode C 破3分鐘低點({low3m:F2})",
                    currentPrice, currentTime, ModeCStage3Ratio, "mode_c_stage3");
            }
            return null;
        }

        /// <summary>
        /// Mode D: percentage stop-loss check.
        /// Trigger: price &lt;= entryPrice × (1 - stopLossPct/100)
        /// </summary>
        public ExitResult CheckModeDStopLoss(
            Position position, double currentPrice, DateTime currentTime)
        {
            double stopPrice = position.EntryPrice * (1.0 - ModeDStopLossPct / 100.0);

            if (currentPrice <= stopPrice)
            {
                System.Console.WriteLine(
                    $"[Mode D STOP LOSS] Time: {currentTime:HH:mm:ss.fff}, Price: {currentPrice:F2}, " +
                    $"EntryPrice: {position.EntryPrice:F2}, StopPrice: {stopPrice:F2} ({ModeDStopLossPct}%)");

                return ExitResult.Create("remaining",
                    $"Mode D 百分比停損({ModeDStopLossPct}%, entry={position.EntryPrice:F2})",
                    currentPrice, currentTime, position.RemainingRatio);
            }
            return null;
        }

        /// <summary>
        /// Mode D: generic minute-low field exit check.
        /// Trigger: price &lt;= field value (e.g. low_1m, low_3m, low_5m, low_7m)
        /// </summary>
        public ExitResult CheckModeDLowExit(
            Position position, TickData row, DateTime currentTime, double currentPrice,
            string field, double exitRatio, string exitLevel)
        {
            double lowPrice = row.GetFieldByName(field);
            if (lowPrice <= 0 || double.IsNaN(lowPrice))
                return null;

            if (currentPrice <= lowPrice)
            {
                System.Console.WriteLine(
                    $"[Mode D {exitLevel}] Time: {currentTime:HH:mm:ss.fff}, Price: {currentPrice:F2}, " +
                    $"{field}: {lowPrice:F2}");

                return ExitResult.Create("trailing_stop",
                    $"Mode D 破{field}低點({lowPrice:F2})",
                    currentPrice, currentTime, exitRatio, exitLevel);
            }
            return null;
        }

        /// <summary>
        /// Mode E: percentage stop-loss check (same logic as Mode D).
        /// Trigger: price &lt;= entryPrice × (1 - stopLossPct/100)
        /// </summary>
        public ExitResult CheckModeEStopLoss(
            Position position, double currentPrice, DateTime currentTime)
        {
            double stopPrice = position.EntryPrice * (1.0 - ModeEStopLossPct / 100.0);

            if (currentPrice <= stopPrice)
            {
                System.Console.WriteLine(
                    $"[Mode E STOP LOSS] Time: {currentTime:HH:mm:ss.fff}, Price: {currentPrice:F2}, " +
                    $"EntryPrice: {position.EntryPrice:F2}, StopPrice: {stopPrice:F2} ({ModeEStopLossPct}%)");

                return ExitResult.Create("remaining",
                    $"Mode E 百分比停損({ModeEStopLossPct}%, entry={position.EntryPrice:F2})",
                    currentPrice, currentTime, position.RemainingRatio);
            }
            return null;
        }

        /// <summary>
        /// Mode E: percentage take-profit target check (upward).
        /// Trigger: price &gt;= entryPrice × (1 + targetPct/100)
        /// </summary>
        public ExitResult CheckModeETargetHit(
            Position position, double currentPrice, DateTime currentTime,
            double targetPct, double exitRatio, string exitLevel)
        {
            double targetPrice = position.EntryPrice * (1.0 + targetPct / 100.0);

            if (currentPrice >= targetPrice)
            {
                System.Console.WriteLine(
                    $"[Mode E {exitLevel}] Time: {currentTime:HH:mm:ss.fff}, Price: {currentPrice:F2}, " +
                    $"EntryPrice: {position.EntryPrice:F2}, Target: {targetPrice:F2} (+{targetPct}%)");

                return ExitResult.Create("trailing_stop",
                    $"Mode E 達到+{targetPct}%目標({targetPrice:F2})",
                    currentPrice, currentTime, exitRatio, exitLevel);
            }
            return null;
        }

        /// <summary>
        /// Mode E: safety net exit (break rolling low → exit all remaining).
        /// Trigger: price &lt;= field value (e.g. low_15m)
        /// </summary>
        public ExitResult CheckModeESafetyNet(
            Position position, TickData row, DateTime currentTime, double currentPrice)
        {
            double lowPrice = row.GetFieldByName(ModeESafetyNetField);
            if (lowPrice <= 0 || double.IsNaN(lowPrice))
                return null;

            if (currentPrice <= lowPrice)
            {
                System.Console.WriteLine(
                    $"[Mode E SAFETY NET] Time: {currentTime:HH:mm:ss.fff}, Price: {currentPrice:F2}, " +
                    $"{ModeESafetyNetField}: {lowPrice:F2}");

                return ExitResult.Create("remaining",
                    $"Mode E 安全網({ModeESafetyNetField}={lowPrice:F2})",
                    currentPrice, currentTime, position.RemainingRatio);
            }
            return null;
        }

        // ===== Config helpers =====

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

        private bool GetBoolConfig(string key, bool defaultVal)
        {
            if (_config.TryGetValue(key, out var v))
            {
                if (v is bool b) return b;
                if (bool.TryParse(v?.ToString(), out bool p)) return p;
            }
            return defaultVal;
        }

        private double GetDoubleConfig(string key, double defaultVal)
        {
            if (_config.TryGetValue(key, out var v))
            {
                if (v is double dv) return dv;
                if (v is float fv) return fv;
                if (v is int iv) return iv;
                if (v is long lv) return lv;
                if (double.TryParse(v?.ToString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double p)) return p;
            }
            return defaultVal;
        }

        private static bool GetBoolFrom(Dictionary<string, object> d, string key, bool defaultVal)
        {
            if (d.TryGetValue(key, out var v))
            {
                if (v is bool b) return b;
                if (bool.TryParse(v?.ToString(), out bool p)) return p;
            }
            return defaultVal;
        }
    }
}
