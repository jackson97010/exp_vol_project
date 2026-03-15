using System;
using System.Collections.Generic;
using StrongestVwap.Core.Models;

namespace StrongestVwap.Strategy
{
    /// <summary>
    /// Signal A: two-phase model — detect near VWAP, then wait for bounce.
    /// Per-stock state tracked in SignalAState.
    /// </summary>
    public class SignalAEvaluator
    {
        private readonly double _vwapNearRatio;
        private readonly double _bounceRatio;
        private readonly TimeSpan _entryStartTime;
        private readonly TimeSpan _entryEndTime;
        private readonly double _maxIncreaseRatio;
        private readonly TimeSpan _preCondStartTime;
        private readonly double _preCondVwapRatio;
        private readonly bool _enabled;

        // Per-stock signal state
        private readonly Dictionary<string, SignalAState> _states = new();

        public SignalAEvaluator(StrategyConfig config)
        {
            _enabled = config.GetBool("signal_a_enabled", true);
            _vwapNearRatio = config.GetDouble("vwap_near_ratio", 1.008);
            _bounceRatio = config.GetDouble("bounce_ratio", 0.008);
            _entryStartTime = config.GetTimeSpan("entry_start_time", new TimeSpan(9, 4, 0));
            _entryEndTime = config.GetTimeSpan("entry_end_time", new TimeSpan(9, 25, 0));
            _maxIncreaseRatio = config.GetDouble("trade_zone_max_increase_ratio", 0.085);
            _preCondStartTime = config.GetTimeSpan("pre_condition_start_time", new TimeSpan(9, 4, 0));
            _preCondVwapRatio = config.GetDouble("pre_condition_vwap_ratio", 0.997);
        }

        /// <summary>
        /// Evaluate Signal A for a stock. Returns true if entry should trigger.
        /// matchInfo must be non-null (stock passed StrongGroup).
        /// </summary>
        public bool Evaluate(string stockId, DateTime time, double price, IndexData idx, MatchInfo matchInfo)
        {
            if (!_enabled) return false;

            var state = GetOrCreateState(stockId);

            // Already triggered — each stock enters at most once
            if (state.Triggered) return false;

            // Already forbidden — permanently blocked today
            if (state.Forbidden) return false;

            // If StrongGroup didn't select this stock, reset near_vwap
            if (matchInfo == null)
            {
                state.Reset();
                return false;
            }

            var tod = time.TimeOfDay;

            // Time window check
            if (tod < _entryStartTime || tod >= _entryEndTime)
                return false;

            // Pre-condition check (after pre_condition_start_time)
            if (tod >= _preCondStartTime && idx.Vwap > 0)
            {
                if (price <= idx.Vwap * _preCondVwapRatio)
                {
                    state.Forbidden = true;
                    return false;
                }
            }

            // Max increase ratio check
            if (idx.PreviousClose > 0)
            {
                double pctChg = (price - idx.PreviousClose) / idx.PreviousClose;
                if (pctChg > _maxIncreaseRatio)
                    return false;
            }

            // Phase 1: detect near VWAP
            if (idx.Vwap > 0 && price / idx.Vwap <= _vwapNearRatio)
            {
                if (!state.NearVwap)
                {
                    state.NearVwap = true;
                    state.LowSinceNear = price;
                }
            }

            // Phase 2: wait for bounce (only after near_vwap)
            if (state.NearVwap)
            {
                if (price < state.LowSinceNear)
                    state.LowSinceNear = price;

                if (state.LowSinceNear > 0)
                {
                    double bounce = (price - state.LowSinceNear) / state.LowSinceNear;
                    if (bounce >= _bounceRatio)
                    {
                        state.Triggered = true;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Reset near_vwap state when StrongGroup no longer selects the stock.
        /// Called externally when matchInfo is null.
        /// </summary>
        public void ResetNearVwap(string stockId)
        {
            if (_states.TryGetValue(stockId, out var state))
                state.Reset();
        }

        public SignalAState GetOrCreateState(string stockId)
        {
            if (!_states.TryGetValue(stockId, out var state))
            {
                state = new SignalAState();
                _states[stockId] = state;
            }
            return state;
        }

        public bool IsTriggered(string stockId)
        {
            return _states.TryGetValue(stockId, out var s) && s.Triggered;
        }

        public bool IsForbidden(string stockId)
        {
            return _states.TryGetValue(stockId, out var s) && s.Forbidden;
        }
    }
}
