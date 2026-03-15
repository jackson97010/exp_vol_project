using System;
using StrongestVwap.Core.Models;

namespace StrongestVwap.Strategy
{
    /// <summary>
    /// Exit logic: stop loss, time exit, take-profit fill check, trailing low, bailout.
    /// </summary>
    public class ExitManager
    {
        private readonly double _stopLossRatioA;
        private readonly double _stopLossPct;
        private readonly double _bailoutRatio;
        private readonly TimeSpan _exitTimeLimit;
        private readonly bool _stopLossEnabled;
        private readonly bool _bailoutEnabled;

        // Trailing low exit
        private readonly bool _trailingLowEnabled;
        private readonly bool _trailingLowRequireTpFill;

        // Rolling low 3-stage exit
        private readonly bool _rollingLowEnabled;
        private readonly string _rollingLowField1;
        private readonly string _rollingLowField2;
        private readonly string _rollingLowField3;

        public ExitManager(StrategyConfig config)
        {
            _stopLossRatioA = config.GetDouble("stop_loss_ratio_a", 0.995);
            _stopLossPct = config.GetDouble("stop_loss_pct", 0);
            _bailoutRatio = config.GetDouble("bailout_ratio", 0.8);
            _exitTimeLimit = config.GetTimeSpan("exit_time_limit", new TimeSpan(13, 20, 0));
            _stopLossEnabled = config.GetBool("stop_loss_enabled", true);
            _bailoutEnabled = config.GetBool("bailout_enabled", true);

            _trailingLowEnabled = config.GetBool("trailing_low_enabled", false);
            _trailingLowRequireTpFill = config.GetBool("trailing_low_require_tp_fill", true);

            _rollingLowEnabled = config.GetBool("rolling_low_exit_enabled", false);
            _rollingLowField1 = config.GetString("rolling_low_field1", "low_1m");
            _rollingLowField2 = config.GetString("rolling_low_field2", "low_3m");
            _rollingLowField3 = config.GetString("rolling_low_field3", "low_5m");
        }

        public bool RollingLowEnabled => _rollingLowEnabled;
        public string RollingLowField1 => _rollingLowField1;
        public string RollingLowField2 => _rollingLowField2;
        public string RollingLowField3 => _rollingLowField3;

        /// <summary>
        /// Check all exit conditions for a trade. Returns exit reason or null.
        /// Also handles partial take-profit fills and trailing low exits.
        /// </summary>
        public string? CheckExit(TradeRecord trade, DateTime time, double price,
            double low10m = 0, double low15m = 0)
        {
            return CheckExitFull(trade, time, price, 0, 0, 0, 0, low10m, low15m, 0);
        }

        /// <summary>
        /// Full exit check with all low fields for rolling low support.
        /// </summary>
        public string? CheckExitFull(TradeRecord trade, DateTime time, double price,
            double low1m, double low3m, double low5m, double low7m,
            double low10m, double low15m, double limitUpPrice)
        {
            // 1. Stop loss (highest priority) — applies to remaining shares
            if (_stopLossEnabled)
            {
                double stopPrice;
                if (_stopLossPct > 0)
                    stopPrice = trade.EntryPrice * (1.0 - _stopLossPct / 100.0);
                else
                    stopPrice = trade.EntryVwap * _stopLossRatioA;

                if (price <= stopPrice)
                {
                    CancelAllPendingOrders(trade);
                    return "stopLoss";
                }
            }

            // 2. Time exit
            if (time.TimeOfDay >= _exitTimeLimit)
            {
                CancelAllPendingOrders(trade);
                return "timeExit";
            }

            // === Rolling low exit mode ===
            if (_rollingLowEnabled)
            {
                // 3a. Limit-up full exit (before rolling low checks)
                if (limitUpPrice > 0 && price >= limitUpPrice)
                {
                    CancelAllPendingOrders(trade);
                    return "limitUp";
                }

                // 3b. Rolling low 3-stage partial exit
                double field1Val = GetLowValue(_rollingLowField1, low1m, low3m, low5m, low7m, low10m, low15m);
                double field2Val = GetLowValue(_rollingLowField2, low1m, low3m, low5m, low7m, low10m, low15m);
                double field3Val = GetLowValue(_rollingLowField3, low1m, low3m, low5m, low7m, low10m, low15m);

                CheckRollingLowExit(trade, time, price, field1Val, field2Val, field3Val);

                if (trade.RemainingShares <= 0)
                    return "rollingLow";

                return null;
            }

            // === Original TP + trailing low mode ===
            // 3. Take-profit fill check
            foreach (var tp in trade.TakeProfitOrders)
            {
                if (!tp.Filled && price >= tp.TargetPrice)
                {
                    tp.Filled = true;
                    tp.FillTime = time;
                    trade.ProfitTaken = true;
                    trade.RemainingShares -= tp.Shares;
                }
            }

            // Check if all shares done (TP + trailing low)
            if (trade.RemainingShares <= 0)
                return "takeProfit";

            // 4. Trailing low exit (partial): low_10m → half, low_15m → rest
            if (_trailingLowEnabled && low10m > 0 && low15m > 0)
            {
                bool canTrigger = !_trailingLowRequireTpFill || trade.ProfitTaken;
                if (canTrigger)
                {
                    CheckTrailingLowExit(trade, time, price, low10m, low15m);

                    if (trade.RemainingShares <= 0)
                        return "trailingLow";
                }
            }

            // 5. Bailout (only after at least one TP fill) — skip if disabled (Mode E)
            if (_bailoutEnabled && trade.ProfitTaken)
            {
                double bailoutPrice = trade.EntryDayHigh * _bailoutRatio;
                if (price <= bailoutPrice)
                {
                    CancelAllPendingOrders(trade);
                    return "bailout";
                }
            }

            return null;
        }

        /// <summary>
        /// Trailing low exit: price <= low_10m sells half of remaining,
        /// price <= low_15m sells all remaining.
        /// If both triggered simultaneously, exits all at once.
        /// </summary>
        private void CheckTrailingLowExit(TradeRecord trade, DateTime time, double price,
            double low10m, double low15m)
        {
            var state = trade.TrailingLow;

            // Stage 0: check low_10m (and possibly low_15m simultaneously)
            if (state.CurrentStage == 0 && price <= low10m)
            {
                if (price <= low15m)
                {
                    // Both triggered at once — exit all remaining
                    state.Low10mExitShares = Math.Floor(trade.RemainingShares / 2.0);
                    state.Low10mExitPrice = price;
                    state.Low10mExitTime = time;

                    state.Low15mExitShares = trade.RemainingShares - state.Low10mExitShares;
                    state.Low15mExitPrice = price;
                    state.Low15mExitTime = time;

                    trade.RemainingShares = 0;
                    state.CurrentStage = 2;

                    CancelAllPendingOrders(trade);
                }
                else
                {
                    // Only low_10m triggered — exit half
                    double halfShares = Math.Floor(trade.RemainingShares / 2.0);
                    state.Low10mExitShares = halfShares;
                    state.Low10mExitPrice = price;
                    state.Low10mExitTime = time;

                    trade.RemainingShares -= halfShares;
                    state.CurrentStage = 1;
                }
            }

            // Stage 1: check low_15m
            if (state.CurrentStage == 1 && price <= low15m)
            {
                state.Low15mExitShares = trade.RemainingShares;
                state.Low15mExitPrice = price;
                state.Low15mExitTime = time;

                trade.RemainingShares = 0;
                state.CurrentStage = 2;

                CancelAllPendingOrders(trade);
            }
        }

        /// <summary>
        /// Rolling low 3-stage exit: field1 → 1/3, field2 → 1/3, field3 → rest.
        /// If multiple stages trigger simultaneously, they cascade in one tick.
        /// </summary>
        private void CheckRollingLowExit(TradeRecord trade, DateTime time, double price,
            double field1Val, double field2Val, double field3Val)
        {
            var state = trade.RollingLow;

            // Stage 0 → 1: price ≤ field1
            if (state.CurrentStage == 0 && field1Val > 0 && price <= field1Val)
            {
                double exitShares = Math.Floor(trade.RemainingShares / 3.0);
                state.Stage1ExitShares = exitShares;
                state.Stage1ExitPrice = price;
                state.Stage1ExitTime = time;
                trade.RemainingShares -= exitShares;
                state.CurrentStage = 1;
            }

            // Stage 1 → 2: price ≤ field2
            if (state.CurrentStage == 1 && field2Val > 0 && price <= field2Val)
            {
                double exitShares = Math.Floor(trade.RemainingShares / 2.0);
                state.Stage2ExitShares = exitShares;
                state.Stage2ExitPrice = price;
                state.Stage2ExitTime = time;
                trade.RemainingShares -= exitShares;
                state.CurrentStage = 2;
            }

            // Stage 2 → 3: price ≤ field3
            if (state.CurrentStage == 2 && field3Val > 0 && price <= field3Val)
            {
                state.Stage3ExitShares = trade.RemainingShares;
                state.Stage3ExitPrice = price;
                state.Stage3ExitTime = time;
                trade.RemainingShares = 0;
                state.CurrentStage = 3;
                CancelAllPendingOrders(trade);
            }
        }

        private static double GetLowValue(string fieldName,
            double low1m, double low3m, double low5m, double low7m,
            double low10m, double low15m)
        {
            return fieldName switch
            {
                "low_1m" => low1m,
                "low_3m" => low3m,
                "low_5m" => low5m,
                "low_7m" => low7m,
                "low_10m" => low10m,
                "low_15m" => low15m,
                _ => 0
            };
        }

        private void CancelAllPendingOrders(TradeRecord trade)
        {
            // Unfilled TP orders remain unfilled; remaining shares will be market-sold
        }
    }
}
