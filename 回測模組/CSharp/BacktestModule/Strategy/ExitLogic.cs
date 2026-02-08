using System;
using System.Collections.Generic;
using System.Globalization;
using BacktestModule.Core.Models;

namespace BacktestModule.Strategy
{
    /// <summary>
    /// Exit result dictionary wrapper. Uses Dictionary&lt;string, object&gt; to match Python dict.
    /// </summary>
    public class ExitResult : Dictionary<string, object>
    {
        public ExitResult() : base() { }

        public static ExitResult Create(
            string exitType, string exitReason, double exitPrice, DateTime exitTime,
            double exitRatio = 1.0, string exitLevel = null)
        {
            var r = new ExitResult
            {
                ["exit_type"] = exitType,
                ["exit_reason"] = exitReason,
                ["exit_price"] = exitPrice,
                ["exit_time"] = exitTime,
                ["exit_ratio"] = exitRatio
            };
            if (exitLevel != null) r["exit_level"] = exitLevel;
            return r;
        }
    }

    /// <summary>
    /// Exit Manager: manages all exit conditions.
    /// </summary>
    public class ExitManager
    {
        private readonly Dictionary<string, object> _config;
        public bool TrailingStopEnabled { get; }
        public List<Dictionary<string, object>> TrailingStopLevels { get; }
        public bool EntryPriceProtection { get; }

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
        }

        /// <summary>
        /// Checks hard stop-loss: Day High at entry - N ticks.
        /// Uses different tick counts for small vs large tick sizes.
        /// </summary>
        public ExitResult CheckHardStopLoss(Position position, double currentPrice, DateTime currentTime)
        {
            double entryTickSize = TickSizeHelper.GetTickSize(position.DayHighAtEntry);

            int stopLossTicks;
            string tickType;
            if (entryTickSize == 0.5 || entryTickSize == 5.0)
            {
                stopLossTicks = GetInt("strategy_b_stop_loss_ticks_large", 3);
                tickType = "large";
            }
            else
            {
                stopLossTicks = GetInt("strategy_b_stop_loss_ticks_small", 4);
                tickType = "small";
            }

            double stopLossPrice = TickSizeHelper.AddTicks(position.DayHighAtEntry, -stopLossTicks);

            if (currentPrice <= stopLossPrice)
            {
                Console.WriteLine(
                    $"[HARD STOP] Time: {currentTime}, Price: {currentPrice:F2}, " +
                    $"StopPrice: {stopLossPrice:F2} (DayHigh={position.DayHighAtEntry:F2} " +
                    $"down {stopLossTicks} ticks, tickType={tickType})");

                var result = ExitResult.Create("remaining", "tick_stop_loss", currentPrice, currentTime);
                result["stop_loss_price"] = stopLossPrice;
                result["stop_loss_ticks"] = stopLossTicks;
                result["tick_type"] = tickType;
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
                Console.WriteLine($"[LIMIT UP EXIT] Time: {currentTime}, Price: {currentPrice:F2}");
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

                Console.WriteLine(
                    $"[MOMENTUM EXIT] Time: {currentTime}, Price: {currentPrice:F2}, " +
                    $"GrowthRate: {growthRate:P2}, Balance: {balanceRatio:F2}, Reason: {reasonText}");

                var result = ExitResult.Create("partial", $"Momentum exhaustion ({reasonText})", currentPrice, currentTime, 0.5);
                result["growth_rate"] = growthRate;
                result["balance_ratio"] = balanceRatio;
                result["buy_weak_streak"] = position.BuyWeakStreak;
                result["growth_drawdown"] = growthDrawdown;
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
                Console.WriteLine($"[FINAL EXIT] Time: {currentTime}, Price: {currentPrice:F2}, {reasonText}");
                var result = ExitResult.Create("remaining", $"Final exit ({reasonText})", currentPrice, currentTime);
                result["exit_threshold"] = exitThreshold;
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
                Console.WriteLine(
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
                    Console.WriteLine(
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
                Console.WriteLine(
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
        /// Integrated exit logic checking all conditions.
        /// </summary>
        public ExitResult CheckNewExitLogic(
            Position position, TickData row,
            DayHighMomentumTracker momentumTracker,
            OrderBookBalanceMonitor orderbookMonitor,
            double limitUpPrice)
        {
            DateTime currentTime = row.Time;
            double currentPrice = row.Price;

            if (currentPrice > position.HighestPrice)
                position.HighestPrice = currentPrice;

            // Check reentry stop-loss
            if (position.PartialExitRecovered)
            {
                var reentryResult = CheckReentryStopLoss(position, row, momentumTracker, orderbookMonitor);
                if (reentryResult != null) return reentryResult;
            }

            if (!TrailingStopEnabled)
            {
                // Momentum half-exit mode
                if (!position.PartialExitDone)
                {
                    var partialResult = CheckPartialExit(position, row, momentumTracker, orderbookMonitor, limitUpPrice);
                    if (partialResult != null) return partialResult;
                }
                else
                {
                    var finalResult = CheckFinalExit(position, row, currentTime, currentPrice);
                    if (finalResult != null) return finalResult;
                }
            }
            else
            {
                // Trailing stop mode
                if (position.RemainingRatio > 0)
                {
                    var limitResult = CheckLimitUpExit(position, currentPrice, currentTime, limitUpPrice);
                    if (limitResult != null)
                    {
                        if (position.RemainingRatio == 1.0)
                            limitResult["exit_ratio"] = 0.5;
                        else
                        {
                            limitResult["exit_ratio"] = position.RemainingRatio;
                            limitResult["exit_type"] = "remaining";
                        }
                        return limitResult;
                    }

                    var stopResult = CheckHardStopLoss(position, currentPrice, currentTime);
                    if (stopResult != null)
                    {
                        stopResult["exit_ratio"] = position.RemainingRatio;
                        stopResult["exit_type"] = "protection";
                        return stopResult;
                    }
                }
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
