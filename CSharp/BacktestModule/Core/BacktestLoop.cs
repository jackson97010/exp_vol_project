using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BacktestModule.Core.Models;
using BacktestModule.Strategy;

namespace BacktestModule.Core
{
    /// <summary>
    /// Main tick-by-tick backtesting loop. The heart of the system.
    /// </summary>
    public class BacktestLoop
    {
        private readonly BacktestEngine _engine;
        private readonly EntryChecker _entryChecker;
        private readonly ExitManager _exitManager;
        private readonly PositionManager _positionManager;
        private readonly ReentryManager _reentryManager;

        // Indicators
        private readonly DayHighMomentumTracker _momentumTracker;
        private readonly OrderBookBalanceMonitor _orderbookMonitor;
        private readonly OutsideVolumeTracker _outsideVolumeTracker;
        private readonly OutsideVolumeTracker _outsideVolumeTracker5s;
        private readonly MassiveMatchingTracker _massiveMatchingTracker;
        private readonly InsideOutsideRatioTracker _insideOutsideRatioTracker;
        private readonly InsideOutsideRatioTracker _largeOrderIoRatioTracker;

        // Configs
        private readonly Dictionary<string, object> _entryConfig;
        private readonly Dictionary<string, object> _exitConfig;
        private readonly Dictionary<string, object> _reentryConfig;

        private bool _lastExitWasStopLoss;
        private int _stopLossCount;
        private DateTime? _lastEntryProcessedTimestamp = null;  // Track last timestamp processed for entry to avoid duplicates

        // Reusable indicators dictionary to avoid per-tick allocation
        private readonly Dictionary<string, double> _indicatorsBuffer = new(12);

        // Cached config values (hot path - avoid Dictionary lookup + type checking per tick)
        private readonly TimeSpan _cfgEntryStartTime;
        private readonly TimeSpan _cfgEntryCutoffTime;
        private readonly bool _cfgEntryBufferEnabled;
        private readonly int _cfgEntryBufferMs;
        private readonly bool _cfgVolumeShrinkSignalEnabled;
        private readonly double _cfgVolumeShrinkRatio;
        private readonly bool _cfgVwapDeviationSignalEnabled;
        private readonly double _cfgVwapDeviationThreshold;
        private readonly bool _cfgRatioIncreaseAfterLossEnabled;
        private readonly double _cfgRatioEntryThreshold;
        private readonly bool _cfgStopLossLimitEnabled;
        private readonly int _cfgMaxStopLossCount;
        private readonly bool _cfgReentry;
        private readonly bool _cfgStopLossResetOnWin;
        private readonly double _cfgAskWallConfirmSeconds;
        private readonly double _cfgAskWallExitRatio;
        private readonly string _cfgVwapColumn;

        public BacktestLoop(BacktestEngine engine)
        {
            _engine = engine;
            _entryChecker = engine.EntryChecker;
            _exitManager = engine.ExitManager;
            _positionManager = engine.PositionManager;
            _reentryManager = engine.ReentryManager;
            _momentumTracker = engine.MomentumTracker;
            _orderbookMonitor = engine.OrderbookMonitor;
            _outsideVolumeTracker = engine.OutsideVolumeTracker;
            _outsideVolumeTracker5s = engine.OutsideVolumeTracker5s;
            _massiveMatchingTracker = engine.MassiveMatchingTracker;
            _insideOutsideRatioTracker = engine.InsideOutsideRatioTracker;
            _largeOrderIoRatioTracker = engine.LargeOrderIoRatioTracker;
            _entryConfig = engine.EntryConfig;
            _exitConfig = engine.ExitConfig;
            _reentryConfig = engine.ReentryConfig;
            _lastExitWasStopLoss = false;

            // Cache frequently accessed config values
            _cfgEntryStartTime = GetTimeSpan(_entryConfig, "entry_start_time", new TimeSpan(9, 9, 0));
            _cfgEntryCutoffTime = GetTimeSpan(_entryConfig, "entry_cutoff_time", new TimeSpan(13, 0, 0));
            _cfgEntryBufferEnabled = GetBool(_entryConfig, "entry_buffer_enabled", false);
            _cfgEntryBufferMs = GetInt(_entryConfig, "entry_buffer_milliseconds", 0);
            _cfgVolumeShrinkSignalEnabled = GetBool(_exitConfig, "volume_shrink_signal_enabled", true);
            _cfgVolumeShrinkRatio = GetDouble(_exitConfig, "volume_shrink_ratio", 5.0);
            _cfgVwapDeviationSignalEnabled = GetBool(_exitConfig, "vwap_deviation_signal_enabled", false);
            _cfgVwapDeviationThreshold = GetDouble(_exitConfig, "vwap_deviation_threshold", 2.0);
            _cfgRatioIncreaseAfterLossEnabled = GetBool(_entryConfig, "ratio_increase_after_loss_enabled", false);
            _cfgRatioEntryThreshold = GetDouble(_entryConfig, "ratio_entry_threshold", 3.0);
            _cfgStopLossLimitEnabled = GetBool(_entryConfig, "stop_loss_limit_enabled", false);
            _cfgMaxStopLossCount = GetInt(_entryConfig, "max_stop_loss_count", 2);
            _cfgReentry = GetBool(_reentryConfig, "reentry", false);
            _cfgStopLossResetOnWin = GetBool(_entryConfig, "stop_loss_reset_on_win", false);
            _cfgAskWallConfirmSeconds = GetDouble(_exitConfig, "ask_wall_confirm_seconds", 15.0);
            _cfgAskWallExitRatio = GetDouble(_exitConfig, "ask_wall_exit_ratio", 0.333);
            _cfgVwapColumn = _exitConfig.TryGetValue("vwap_column", out var vc) ? vc?.ToString() ?? "vwap" : "vwap";
        }

        /// <summary>
        /// Run the backtest loop over all tick data.
        /// </summary>
        public List<TradeRecord> Run(List<TickData> data, string stockId, double refPrice, double limitUpPrice)
        {
            var state = InitLoopState();
            // Store massive threshold in state for ask wall signal detection
            state.MassiveThreshold = ResolveMassiveThreshold();
            var metrics = new MetricsAccumulator(data.Count);

            // Pre-filter trade data for SmallOrderFilter (avoids repeated .Where().ToList() per entry check)
            _entryChecker.SmallOrderFilter.PreFilterTradeData(data);

            for (int i = 0; i < data.Count; i++)
            {
                var row = data[i];

                UpdateCurrentState(row, state);
                UpdateIndicators(row, state);
                CalculateMetrics(row, state, metrics);

                double currentPrice = row.Price;

                if (_positionManager.HasPosition())
                {
                    ProcessExitLogic(state, row, limitUpPrice);
                }
                else
                {
                    // Entry logic only for trade data (price > 0), ignore depth data (price = 0)
                    if (currentPrice > 0)
                    {
                        ProcessEntryLogic(row, stockId, refPrice, limitUpPrice, state, data);
                    }
                }

                // Update prev_day_high for next tick's comparison (only for trade data)
                if (currentPrice > 0)
                {
                    state.PrevDayHigh = state.CurrentDayHigh;
                }
            }

            ForceCloseAtMarketClose(state);
            AddMetricsToData(data, metrics);
            return _positionManager.TradeHistory;
        }

        private LoopState InitLoopState()
        {
            return new LoopState
            {
                RunningDayHigh = 0,  // Initialize to 0 so the first price becomes the day high
                PrevDayHigh = 0,
                CurrentDayHigh = 0
            };
        }

        private void UpdateCurrentState(TickData row, LoopState state)
        {
            state.CurrentTime = row.Time;
            state.CurrentPrice = row.Price;

            // CRITICAL FIX: Track the actual running maximum price of the day
            // Update running day high if current price is higher (only for trade data)
            if (row.Price > 0)
            {
                if (row.Price > state.RunningDayHigh)
                {
                    // DON'T update PrevDayHigh here! It should be updated in the main loop
                    // Just update the running maximum
                    state.RunningDayHigh = row.Price;
                    // System.Console.WriteLine($"[DEBUG] Day High Update: {state.CurrentDayHigh:F2} -> {state.RunningDayHigh:F2} at {row.Time:HH:mm:ss.fff}");
                }
            }

            // CRITICAL FIX: Use the day_high from parquet data, NOT the running high from price
            // The parquet data contains aggregated day high from multiple sources (trades + depth)
            // which can be higher than the current trade price
            state.CurrentDayHigh = row.DayHigh;  // Direct use of Parquet data field

            state.CurrentTickType = row.TickType;
            state.CurrentVolume = row.Volume;

            double currentBidAskRatio = row.BidAskRatio;
            if (!double.IsNaN(currentBidAskRatio) && currentBidAskRatio > 0)
            {
                state.LastBidAskRatio = currentBidAskRatio;
            }
        }

        private void UpdateIndicators(TickData row, LoopState state)
        {
            int tickType = state.CurrentTickType;

            _outsideVolumeTracker.UpdateTrades(
                state.CurrentTime.Value, tickType, state.CurrentPrice, state.CurrentVolume);
            _outsideVolumeTracker5s.UpdateTrades(
                state.CurrentTime.Value, tickType, state.CurrentPrice, state.CurrentVolume);
            _massiveMatchingTracker.Update(
                state.CurrentTime.Value, tickType, state.CurrentPrice, state.CurrentVolume);
            _insideOutsideRatioTracker.Update(
                state.CurrentTime.Value, tickType, state.CurrentPrice, state.CurrentVolume);
            _largeOrderIoRatioTracker.Update(
                state.CurrentTime.Value, tickType, state.CurrentPrice, state.CurrentVolume);
            _momentumTracker.Update(state.CurrentTime.Value, state.CurrentDayHigh);
        }

        private void CalculateMetrics(TickData row, LoopState state, MetricsAccumulator metrics)
        {
            double growthRate = _momentumTracker.GetGrowthRate();
            metrics.DayHighGrowthRates.Add(growthRate);

            double bidAvg = _orderbookMonitor.CalculateBidThickness(row);
            double askAvg = _orderbookMonitor.CalculateAskThickness(row);
            double balanceRatio = _orderbookMonitor.CalculateBalanceRatio(row);
            metrics.BidAvgVolumes.Add(bidAvg);
            metrics.AskAvgVolumes.Add(askAvg);
            metrics.BalanceRatios.Add(balanceRatio);

            double ioRatio = _insideOutsideRatioTracker.GetRatio();
            double outsideRatio = _insideOutsideRatioTracker.GetOutsideRatio();
            metrics.InsideOutsideRatios.Add(ioRatio);
            metrics.OutsideRatios.Add(outsideRatio);

            double largeIoRatio = _largeOrderIoRatioTracker.GetRatio();
            double largeOutsideRatio = _largeOrderIoRatioTracker.GetOutsideRatio();
            metrics.LargeOrderIoRatios.Add(largeIoRatio);
            metrics.LargeOrderOutsideRatios.Add(largeOutsideRatio);

            bool isBreakout = _entryChecker.CheckDayHighBreakout(state.CurrentDayHigh, state.PrevDayHigh);
            metrics.DayHighBreakouts.Add(isBreakout);

            // Observation signals: volume shrink + VWAP deviation
            bool volumeShrink = false;
            bool vwapDeviation = false;
            var position = _positionManager.GetCurrentPosition();

            if (position != null && state.CurrentPrice > 0)
            {
                // Signal 1: 價漲量縮 (price new high but 5s outside volume shrinking)
                if (_cfgVolumeShrinkSignalEnabled)
                {
                    if (state.CurrentPrice > position.HighestPrice)
                    {
                        double current5sVol = _outsideVolumeTracker5s.GetCurrentVolume();
                        double entry5sVol = position.EntryOutsideVolume5s;
                        if (entry5sVol > 0 && current5sVol < entry5sVol / _cfgVolumeShrinkRatio)
                        {
                            volumeShrink = true;
                        }
                    }
                }

                // Signal 2: VWAP 乖離 (price deviates from VWAP above threshold)
                if (_cfgVwapDeviationSignalEnabled)
                {
                    double vwap = row.Vwap;
                    if (vwap > 0)
                    {
                        double deviationPct = (state.CurrentPrice - vwap) / vwap * 100;
                        if (deviationPct >= _cfgVwapDeviationThreshold)
                        {
                            vwapDeviation = true;
                        }
                    }
                }
            }

            metrics.VolumeShrinkSignals.Add(volumeShrink);
            metrics.VwapDeviationSignals.Add(vwapDeviation);
        }

        private void ProcessExitLogic(LoopState state, TickData row, double limitUpPrice)
        {
            var position = _positionManager.GetCurrentPosition();
            DateTime currentTime = state.CurrentTime.Value;
            double currentPrice = state.CurrentPrice;

            double prevHighestPrice = position.HighestPrice;
            if (currentPrice > position.HighestPrice)
                position.HighestPrice = currentPrice;

            // Limit-up exit (priority in all modes)
            if (limitUpPrice > 0 && currentPrice >= limitUpPrice)
            {
                string exitReason = "Limit-up exit";
                string exitType;
                double exitRatio;
                if (position.RemainingRatio < 1.0)
                {
                    exitType = "remaining";
                    exitRatio = position.RemainingRatio;
                }
                else
                {
                    exitType = "partial";
                    exitRatio = 0.5;
                }

                var exitResult = ExitResult.Create(exitType, exitReason, limitUpPrice, currentTime, exitRatio);
                HandleExit(exitResult, currentTime, state);
                return;
            }

            // Mode C branch (three-stage exit)
            if (_exitManager.ModeCEnabled)
            {
                ProcessModeCExit(state, row, limitUpPrice);
                return;
            }

            // Mode D branch (percentage stop-loss + minute-low staged exit)
            if (_exitManager.ModeDEnabled)
            {
                ProcessModeDExit(state, row, limitUpPrice);
                return;
            }

            // Mode E branch (percentage take-profit + safety net)
            if (_exitManager.ModeEEnabled)
            {
                ProcessModeEExit(state, row, limitUpPrice);
                return;
            }

            bool useTrailingStop = _exitManager.TrailingStopEnabled;

            // Trailing stop mode
            if (useTrailingStop)
            {
                var trailingResult = _exitManager.CheckTrailingStop(position, row, currentTime, currentPrice);
                if (trailingResult != null)
                {
                    HandleTrailingExit(trailingResult);
                    position = _positionManager.GetCurrentPosition();
                    if (position != null && position.RemainingRatio <= 0)
                    {
                        ClosePositionCompletely(position, trailingResult, state);
                    }
                    return;
                }
            }

            // Ask wall exit (same priority as trailing stop, OR relationship)
            position = _positionManager.GetCurrentPosition();
            if (position != null)
            {
                var askWallResult = CheckAskWallExit(position, row, state, currentTime, currentPrice);
                if (askWallResult != null)
                {
                    HandleTrailingExit(askWallResult);
                    position = _positionManager.GetCurrentPosition();
                    if (position != null && position.RemainingRatio <= 0)
                    {
                        ClosePositionCompletely(position, askWallResult, state);
                    }
                    return;
                }
            }

            // Entry price protection (after trailing stop / ask wall)
            if (useTrailingStop)
            {
                position = _positionManager.GetCurrentPosition();
                if (position != null)
                {
                    var protectionResult = _exitManager.CheckEntryPriceProtection(position, currentPrice, currentTime);
                    if (protectionResult != null)
                    {
                        HandleExit(protectionResult, currentTime, state);
                        return;
                    }
                }
            }

            // Momentum half-exit mode (only if trailing stop disabled)
            if (!useTrailingStop)
            {
                position = _positionManager.GetCurrentPosition();
                if (position != null)
                {
                    var exitResult = _exitManager.CheckMomentumExhaustion(
                        position, row, _momentumTracker, _orderbookMonitor, currentTime, currentPrice);
                    if (exitResult != null)
                    {
                        HandleExit(exitResult, currentTime, state);
                        return;
                    }

                    exitResult = _exitManager.CheckFinalExit(position, row, currentTime, currentPrice);
                    if (exitResult != null)
                    {
                        HandleExit(exitResult, currentTime, state);
                        return;
                    }
                }
            }

            // VWAP deviation exit (both modes, if enabled)
            position = _positionManager.GetCurrentPosition();
            if (position != null)
            {
                double vwapValue = row.GetFieldByName(_cfgVwapColumn);
                var vwapResult = _exitManager.CheckVwapDeviationExit(position, currentPrice, currentTime, vwapValue);
                if (vwapResult != null)
                {
                    HandleExit(vwapResult, currentTime, state);
                    return;
                }
            }

            // High 3-min drawdown exit (both modes, if enabled)
            position = _positionManager.GetCurrentPosition();
            if (position != null)
            {
                var drawdownResult = _exitManager.CheckHigh3MinDrawdownExit(position, currentPrice, currentTime, row.High3m);
                if (drawdownResult != null)
                {
                    HandleExit(drawdownResult, currentTime, state);
                    return;
                }
            }

            // Hard stop (both modes)
            position = _positionManager.GetCurrentPosition();
            if (position != null)
            {
                var exitResult = _exitManager.CheckHardStopLoss(position, currentPrice, currentTime);
                if (exitResult != null)
                {
                    HandleExit(exitResult, currentTime, state);
                }
            }

            // Reentry check
            position = _positionManager.GetCurrentPosition();
            if (position != null
                && _cfgReentry
                && position.AllowReentry
                && position.RemainingRatio < 1.0)
            {
                CheckReentry(position, row, state, prevHighestPrice);
            }
        }

        /// <summary>
        /// Resolves the massive threshold from dynamic or fixed config.
        /// </summary>
        private double ResolveMassiveThreshold()
        {
            if (_entryConfig.TryGetValue("dynamic_liquidity_resolved_threshold", out var rt))
            {
                double resolved = Convert.ToDouble(rt);
                if (resolved > 0) return resolved;
            }
            return GetDouble(_entryConfig, "massive_matching_amount", 50000000.0);
        }

        /// <summary>
        /// Checks ask wall exit with observation/confirmation state machine.
        /// Mirrors Python: _check_ask_wall_exit()
        /// </summary>
        private ExitResult CheckAskWallExit(Position position, TickData row, LoopState state,
            DateTime currentTime, double currentPrice)
        {
            var askWallState = state.AskWall;

            // Already triggered for this position, skip
            if (askWallState.Triggered)
                return null;

            if (!askWallState.Active)
            {
                // Not in observation: detect signal
                bool signal = _exitManager.CheckAskWallSignal(row, currentTime, currentPrice, state.MassiveThreshold);
                if (signal)
                {
                    askWallState.Active = true;
                    askWallState.TriggerTime = currentTime;
                    askWallState.TriggerDayHigh = state.CurrentDayHigh;
                    System.Console.WriteLine(
                        $"[大壓單觀察開始] 時間: {currentTime:HH:mm:ss.fff}, " +
                        $"day_high: {state.CurrentDayHigh:F2}, 等待 {_cfgAskWallConfirmSeconds}s 確認");
                }
                return null;
            }
            else
            {
                // In observation: check if day_high has made new high → cancel
                if (state.CurrentDayHigh > askWallState.TriggerDayHigh)
                {
                    System.Console.WriteLine(
                        $"[大壓單觀察取消] 時間: {currentTime:HH:mm:ss.fff}, " +
                        $"day_high 創新高: {state.CurrentDayHigh:F2} > 觸發時: {askWallState.TriggerDayHigh:F2}");

                    askWallState.Active = false;
                    askWallState.TriggerTime = null;
                    askWallState.TriggerDayHigh = 0.0;

                    // Re-detect immediately on same tick
                    bool signal = _exitManager.CheckAskWallSignal(row, currentTime, currentPrice, state.MassiveThreshold);
                    if (signal)
                    {
                        askWallState.Active = true;
                        askWallState.TriggerTime = currentTime;
                        askWallState.TriggerDayHigh = state.CurrentDayHigh;
                        System.Console.WriteLine(
                            $"[大壓單觀察重新開始] 時間: {currentTime:HH:mm:ss.fff}, " +
                            $"day_high: {state.CurrentDayHigh:F2}, 等待 {_cfgAskWallConfirmSeconds}s 確認");
                    }
                    return null;
                }

                // Check if observation period has elapsed
                double elapsed = (currentTime - askWallState.TriggerTime.Value).TotalSeconds;

                if (elapsed >= _cfgAskWallConfirmSeconds)
                {
                    askWallState.Active = false;
                    askWallState.Triggered = true;

                    System.Console.WriteLine(
                        $"[大壓單出場確認] 時間: {currentTime:HH:mm:ss.fff}, 價格: {currentPrice:F2}, " +
                        $"觀察期: {elapsed:F1}s, day_high 未創新高, 出場比例: {_cfgAskWallExitRatio:P0}");

                    var result = ExitResult.Create("trailing_stop",
                        $"大壓單出場(觀察{elapsed:F0}s day_high未創新高)",
                        currentPrice, currentTime, _cfgAskWallExitRatio, "ask_wall");
                    return result;
                }

                return null;
            }
        }

        /// <summary>
        /// Mode C three-stage exit logic.
        /// Stage 0: ask wall → exit 1/3 (hard stop loss) OR fallback low_1m → exit 1/3 (hard stop loss)
        /// Stage 1: vwap_5m deviation → exit 1/3 (hard stop loss)
        /// Stage 2: low_3m → exit remaining (entry price protection, no hard stop loss)
        /// Mirrors Python: _process_mode_c_exit()
        /// </summary>
        private void ProcessModeCExit(LoopState state, TickData row, double limitUpPrice)
        {
            var position = _positionManager.GetCurrentPosition();
            if (position == null) return;

            DateTime currentTime = state.CurrentTime.Value;
            double currentPrice = state.CurrentPrice;
            var mc = state.ModeC;
            int currentStage = mc.CurrentStage;

            // ── Stage 0: waiting for Stage1 trigger ──
            if (currentStage == 0)
            {
                // Hard stop loss (Stage 0-1)
                var stopResult = _exitManager.CheckHardStopLoss(position, currentPrice, currentTime);
                if (stopResult != null)
                {
                    HandleExit(stopResult, currentTime, state);
                    return;
                }

                // Ask wall path
                var askWallResult = CheckAskWallExit(position, row, state, currentTime, currentPrice);
                if (askWallResult != null)
                {
                    // Ask wall triggered → Stage1 exit
                    mc.AskWallTriggered = true;
                    mc.PathDecided = true;
                    mc.CurrentStage = 1;
                    // Override level and ratio for mode_c_stage1
                    askWallResult.ExitLevel = "mode_c_stage1";
                    askWallResult.ExitRatio = _exitManager.ModeCStage1Ratio;
                    string origReason = askWallResult.ExitReason ?? "";
                    askWallResult.ExitReason = $"Mode C 大壓單出場({origReason})";

                    HandleTrailingExit(askWallResult);
                    position = _positionManager.GetCurrentPosition();
                    if (position != null && position.RemainingRatio <= 0)
                    {
                        ClosePositionCompletely(position, askWallResult, state);
                    }
                    return;
                }

                // Fallback: break low_1m
                if (!mc.PathDecided)
                {
                    var low1mResult = _exitManager.CheckLow1mExit(position, row, currentTime, currentPrice);
                    if (low1mResult != null)
                    {
                        mc.PathDecided = true;
                        mc.CurrentStage = 1;
                        HandleTrailingExit(low1mResult);
                        position = _positionManager.GetCurrentPosition();
                        if (position != null && position.RemainingRatio <= 0)
                        {
                            ClosePositionCompletely(position, low1mResult, state);
                        }
                        return;
                    }
                }
            }

            // ── Stage 1: waiting for Stage2 trigger ──
            else if (currentStage == 1)
            {
                // Hard stop loss (Stage 1) — optionally tightened by 1 tick after Stage 1 exit
                int tickOffset = _exitManager.ModeCTightenStopAfterStage1 ? -1 : 0;
                var stopResult = _exitManager.CheckHardStopLoss(position, currentPrice, currentTime, tickOffset);
                if (stopResult != null)
                {
                    HandleExit(stopResult, currentTime, state);
                    return;
                }

                // VWAP_5m negative deviation
                var vwapResult = _exitManager.CheckVwap5mDeviationExit(position, row, currentTime, currentPrice);
                if (vwapResult != null)
                {
                    mc.CurrentStage = 2;
                    HandleTrailingExit(vwapResult);
                    position = _positionManager.GetCurrentPosition();
                    if (position != null && position.RemainingRatio <= 0)
                    {
                        ClosePositionCompletely(position, vwapResult, state);
                    }
                    return;
                }
            }

            // ── Stage 2: waiting for Stage3 trigger ──
            else if (currentStage == 2)
            {
                // Entry price protection only (no hard stop loss in Stage 2)
                var protectionResult = _exitManager.CheckEntryPriceProtection(position, currentPrice, currentTime);
                if (protectionResult != null)
                {
                    HandleExit(protectionResult, currentTime, state);
                    return;
                }

                // Break low_3m
                var low3mResult = _exitManager.CheckLow3mExit(position, row, currentTime, currentPrice);
                if (low3mResult != null)
                {
                    mc.CurrentStage = 3;  // All stages complete
                    HandleTrailingExit(low3mResult);
                    position = _positionManager.GetCurrentPosition();
                    if (position != null && position.RemainingRatio <= 0)
                    {
                        ClosePositionCompletely(position, low3mResult, state);
                    }
                    return;
                }
            }
        }

        /// <summary>
        /// Mode D three-stage exit logic.
        /// Stage 0: percentage stop-loss (full exit) OR break stage1_field → exit 1/3
        /// Stage 1: percentage stop-loss (full exit) OR break stage2_field → exit 1/3
        /// Stage 2: entry price protection (full exit) OR break stage3_field → exit remaining
        /// </summary>
        private void ProcessModeDExit(LoopState state, TickData row, double limitUpPrice)
        {
            var position = _positionManager.GetCurrentPosition();
            if (position == null) return;

            DateTime currentTime = state.CurrentTime.Value;
            double currentPrice = state.CurrentPrice;
            var md = state.ModeD;
            int currentStage = md.CurrentStage;

            // ── Stage 0: waiting for Stage1 trigger ──
            if (currentStage == 0)
            {
                // Percentage stop-loss (Stage 0)
                var stopResult = _exitManager.CheckModeDStopLoss(position, currentPrice, currentTime);
                if (stopResult != null)
                {
                    HandleExit(stopResult, currentTime, state);
                    return;
                }

                // Break stage1_field (e.g. low_1m)
                var lowResult = _exitManager.CheckModeDLowExit(
                    position, row, currentTime, currentPrice,
                    _exitManager.ModeDStage1Field, _exitManager.ModeDStage1Ratio, "mode_d_stage1");
                if (lowResult != null)
                {
                    md.CurrentStage = 1;
                    HandleTrailingExit(lowResult);
                    position = _positionManager.GetCurrentPosition();
                    if (position != null && position.RemainingRatio <= 0)
                    {
                        ClosePositionCompletely(position, lowResult, state);
                    }
                    return;
                }
            }

            // ── Stage 1: waiting for Stage2 trigger ──
            else if (currentStage == 1)
            {
                // Percentage stop-loss (Stage 1)
                var stopResult = _exitManager.CheckModeDStopLoss(position, currentPrice, currentTime);
                if (stopResult != null)
                {
                    HandleExit(stopResult, currentTime, state);
                    return;
                }

                // Break stage2_field (e.g. low_3m)
                var lowResult = _exitManager.CheckModeDLowExit(
                    position, row, currentTime, currentPrice,
                    _exitManager.ModeDStage2Field, _exitManager.ModeDStage2Ratio, "mode_d_stage2");
                if (lowResult != null)
                {
                    md.CurrentStage = 2;
                    HandleTrailingExit(lowResult);
                    position = _positionManager.GetCurrentPosition();
                    if (position != null && position.RemainingRatio <= 0)
                    {
                        ClosePositionCompletely(position, lowResult, state);
                    }
                    return;
                }
            }

            // ── Stage 2: waiting for Stage3 trigger ──
            else if (currentStage == 2)
            {
                // Entry price protection only (no percentage stop-loss in Stage 2)
                var protectionResult = _exitManager.CheckEntryPriceProtection(position, currentPrice, currentTime);
                if (protectionResult != null)
                {
                    HandleExit(protectionResult, currentTime, state);
                    return;
                }

                // Break stage3_field (e.g. low_5m)
                var lowResult = _exitManager.CheckModeDLowExit(
                    position, row, currentTime, currentPrice,
                    _exitManager.ModeDStage3Field, _exitManager.ModeDStage3Ratio, "mode_d_stage3");
                if (lowResult != null)
                {
                    md.CurrentStage = 3;  // All stages complete
                    HandleTrailingExit(lowResult);
                    position = _positionManager.GetCurrentPosition();
                    if (position != null && position.RemainingRatio <= 0)
                    {
                        ClosePositionCompletely(position, lowResult, state);
                    }
                    return;
                }
            }
        }

        /// <summary>
        /// Mode E exit: percentage take-profit targets (upward) + safety net (downward).
        /// Targets are checked in order; each triggers partial exit when price reaches upward.
        /// Safety net (low_15m): if price falls to rolling low, exit all remaining.
        /// Stop-loss: percentage-based full exit (same as Mode D).
        /// </summary>
        private void ProcessModeEExit(LoopState state, TickData row, double limitUpPrice)
        {
            var position = _positionManager.GetCurrentPosition();
            if (position == null) return;

            DateTime currentTime = state.CurrentTime.Value;
            double currentPrice = state.CurrentPrice;
            var me = state.ModeE;

            // 1. Stop-loss check (always active)
            var stopResult = _exitManager.CheckModeEStopLoss(position, currentPrice, currentTime);
            if (stopResult != null)
            {
                HandleExit(stopResult, currentTime, state);
                return;
            }

            // 2. Take-profit targets (upward triggers, checked in order)
            if (!me.Target1Reached)
            {
                var t1 = _exitManager.CheckModeETargetHit(
                    position, currentPrice, currentTime,
                    _exitManager.ModeETarget1Pct, _exitManager.ModeETarget1Ratio, "mode_e_target1");
                if (t1 != null)
                {
                    me.Target1Reached = true;
                    HandleTrailingExit(t1);
                    position = _positionManager.GetCurrentPosition();
                    if (position == null || position.RemainingRatio <= 0)
                    {
                        ClosePositionCompletely(position, t1, state);
                        return;
                    }
                }
            }

            if (!me.Target2Reached)
            {
                var t2 = _exitManager.CheckModeETargetHit(
                    position, currentPrice, currentTime,
                    _exitManager.ModeETarget2Pct, _exitManager.ModeETarget2Ratio, "mode_e_target2");
                if (t2 != null)
                {
                    me.Target2Reached = true;
                    HandleTrailingExit(t2);
                    position = _positionManager.GetCurrentPosition();
                    if (position == null || position.RemainingRatio <= 0)
                    {
                        ClosePositionCompletely(position, t2, state);
                        return;
                    }
                }
            }

            if (!me.Target3Reached)
            {
                var t3 = _exitManager.CheckModeETargetHit(
                    position, currentPrice, currentTime,
                    _exitManager.ModeETarget3Pct, _exitManager.ModeETarget3Ratio, "mode_e_target3");
                if (t3 != null)
                {
                    me.Target3Reached = true;
                    HandleTrailingExit(t3);
                    position = _positionManager.GetCurrentPosition();
                    if (position == null || position.RemainingRatio <= 0)
                    {
                        ClosePositionCompletely(position, t3, state);
                        return;
                    }
                }
            }

            // 3. Safety net: break rolling low → exit all remaining
            position = _positionManager.GetCurrentPosition();
            if (position != null && position.RemainingRatio > 0)
            {
                var safetyResult = _exitManager.CheckModeESafetyNet(position, row, currentTime, currentPrice);
                if (safetyResult != null)
                {
                    HandleExit(safetyResult, currentTime, state);
                    return;
                }
            }
        }

        private void ProcessEntryLogic(TickData row, string stockId, double refPrice,
            double limitUpPrice, LoopState state, List<TickData> data)
        {
            DateTime currentTime = row.Time;
            int currentTickType = row.TickType;

            // Skip if we've already processed this exact timestamp (handle duplicate ticks)
            if (_lastEntryProcessedTimestamp.HasValue &&
                _lastEntryProcessedTimestamp.Value == currentTime)
            {
                return;  // Skip duplicate timestamp
            }
            _lastEntryProcessedTimestamp = currentTime;

            // Stop-loss limit check: skip entry if exceeded max stop-loss count
            if (_cfgStopLossLimitEnabled && _stopLossCount >= _cfgMaxStopLossCount)
            {
                return;
            }

            var waitingState = state.WaitingForOutsideEntry;
            var bufferState = state.BreakoutBuffer;

            bool isBreakout = _entryChecker.CheckDayHighBreakout(state.CurrentDayHigh, state.PrevDayHigh);

            // Check if in waiting-for-outside-entry state
            if (waitingState.Active)
            {
                bool isOutsideTick = (currentTickType == 1);

                bool differentTimestamp = false;
                if (waitingState.BreakoutTime.HasValue)
                {
                    var timeDiff = Math.Abs((currentTime - waitingState.BreakoutTime.Value).TotalMilliseconds);
                    differentTimestamp = timeDiff > 0.001;
                }

                if (isOutsideTick && differentTimestamp)
                {
                    waitingState.Active = false;

                    if (_cfgEntryBufferEnabled)
                    {
                        bufferState.Active = true;
                        bufferState.StartTime = currentTime;
                        bufferState.DayHigh = waitingState.BreakoutDayHigh;
                        bufferState.Checked = false;
                    }
                }
                else
                {
                    // Stay in waiting state; update if new breakout
                    if (isBreakout)
                    {
                        state.DayHighBreakCount++;
                        waitingState.BreakoutTime = currentTime;
                        waitingState.BreakoutDayHigh = state.CurrentDayHigh;
                        waitingState.PrevDayHigh = state.PrevDayHigh;
                    }
                    // Only return if still waiting
                    return;
                }
            }

            // Handle new breakout (when not already waiting)
            if (isBreakout && !waitingState.Active)
            {
                state.DayHighBreakCount++;

                // Enter waiting state
                waitingState.Active = true;
                waitingState.BreakoutTime = currentTime;
                waitingState.BreakoutDayHigh = state.CurrentDayHigh;
                waitingState.PrevDayHigh = state.PrevDayHigh;

                // Traditional buffer mechanism
                if (_cfgEntryBufferEnabled)
                {
                    bufferState.Active = true;
                    bufferState.StartTime = row.Time;
                    bufferState.DayHigh = state.CurrentDayHigh;
                    bufferState.Checked = false;
                }

                return;
            }

            // Ratio threshold escalation after stop-loss
            double? fixedRatioThreshold = state.FixedRatioThreshold;
            double? dynamicRatioThreshold = state.DynamicRatioThreshold;

            if (_cfgRatioIncreaseAfterLossEnabled && _lastExitWasStopLoss)
            {
                double lastEntryRatio = state.LastEntryRatio;
                double defaultThreshold = _cfgRatioEntryThreshold;
                double? minThreshold = _entryConfig.TryGetValue("ratio_increase_min_threshold", out var mt)
                    ? Convert.ToDouble(mt) : (double?)null;

                var candidates = new List<double>();
                if (minThreshold.HasValue) candidates.Add(minThreshold.Value);
                if (lastEntryRatio > 0) candidates.Add(lastEntryRatio);

                double threshold = candidates.Count > 0 ? candidates.Min() : defaultThreshold;
                threshold = Math.Max(defaultThreshold, threshold);

                fixedRatioThreshold = threshold;
                dynamicRatioThreshold = threshold;
            }

            var entrySignal = CheckEntry(
                row, stockId, refPrice, limitUpPrice,
                state.PrevDayHigh, state.LastBidAskRatio,
                fixedRatioThreshold, dynamicRatioThreshold,
                state.FirstEntryOutsideVolume, isBreakout,
                bufferState, data, state);

            if (entrySignal != null && entrySignal.Passed)
            {
                ExecuteEntry(entrySignal, state, row);
                if (bufferState.Active)
                {
                    bufferState.Active = false;
                    bufferState.StartTime = null;
                }
                _lastExitWasStopLoss = false;
            }
        }

        private EntrySignal CheckEntry(
            TickData row, string stockId, double refPrice, double limitUpPrice,
            double prevDayHigh, double? lastBidAskRatio,
            double? fixedRatioThreshold, double? dynamicRatioThreshold,
            double? firstEntryOutsideVolume, bool isBreakout,
            BreakoutBufferState bufferState, List<TickData> data, LoopState state)
        {
            DateTime currentTime = row.Time;

            // Buffer logic
            bool bufferActive = false;
            if (_cfgEntryBufferEnabled && bufferState != null)
            {
                var startTime = bufferState.StartTime;
                if (bufferState.Active && startTime.HasValue && _cfgEntryBufferMs > 0)
                {
                    double deltaMs = (currentTime - startTime.Value).TotalMilliseconds;
                    if (deltaMs <= _cfgEntryBufferMs)
                        bufferActive = true;
                    else
                    {
                        bufferState.Active = false;
                        bufferState.StartTime = null;
                    }
                }
            }

            bool effectiveBreakout = isBreakout || bufferActive;
            if (!effectiveBreakout)
                return null;

            // Time checks (using cached config values)
            if (currentTime.TimeOfDay < _cfgEntryStartTime)
                return null;

            if (currentTime.TimeOfDay >= _cfgEntryCutoffTime)
                return null;

            // Reuse indicators buffer to avoid per-tick allocation
            _indicatorsBuffer.Clear();
            _indicatorsBuffer["ratio"] = row.Ratio15s300s;
            _indicatorsBuffer["pct_2min"] = row.Pct2Min;
            _indicatorsBuffer["pct_3min"] = row.Pct3Min;
            _indicatorsBuffer["pct_5min"] = row.Pct5Min;
            _indicatorsBuffer["low_1m"] = row.Low1m > 0 ? row.Low1m : row.Price;
            _indicatorsBuffer["low_3m"] = row.Low3m > 0 ? row.Low3m : row.Price;
            _indicatorsBuffer["low_5m"] = row.Low5m > 0 ? row.Low5m : row.Price;
            _indicatorsBuffer["low_3min"] = row.Low3m > 0 ? row.Low3m : row.Price;
            _indicatorsBuffer["low_10min"] = row.Low10m > 0 ? row.Low10m : row.Price;
            _indicatorsBuffer["low_15min"] = row.Low15m > 0 ? row.Low15m : row.Price;
            var indicators = _indicatorsBuffer;

            // Resolve bid/ask ratio (use current or last tracked valid value)
            double askBidRatio;
            double currentBidAskRatio = row.BidAskRatio;
            if (!double.IsNaN(currentBidAskRatio) && currentBidAskRatio > 0)
            {
                askBidRatio = currentBidAskRatio;
            }
            else
            {
                askBidRatio = lastBidAskRatio ?? 0.0;
            }

            double massiveMatchingAmount = _massiveMatchingTracker.GetMassiveMatchingAmount();

            return _entryChecker.CheckEntrySignals(
                stockId: stockId,
                currentPrice: row.Price,
                currentTime: currentTime,
                prevDayHigh: prevDayHigh,
                indicators: indicators,
                askBidRatio: askBidRatio,
                refPrice: refPrice,
                massiveMatchingAmount: massiveMatchingAmount,
                fixedRatioThreshold: fixedRatioThreshold,
                dynamicRatioThreshold: dynamicRatioThreshold,
                minOutsideAmount: null,
                forceLog: true,
                tickData: data,
                currentRow: row,
                isDayHighBreakout: effectiveBreakout,
                lastExitTime: state.LastExitTime);
        }

        private void ExecuteEntry(EntrySignal entrySignal, LoopState state, TickData row)
        {
            double entryRatio = row.Ratio15s300s;
            double bidThickness = row.BidAvgVolume;
            double outsideVolume = _outsideVolumeTracker.GetCurrentVolume();
            double outsideVolume5s = _outsideVolumeTracker5s.GetCurrentVolume();

            var position = _positionManager.OpenPosition(
                entryTime: entrySignal.Time,
                entryPrice: entrySignal.Price,
                entryBidThickness: bidThickness,
                dayHighAtEntry: state.CurrentDayHigh,
                entryRatio: entryRatio,
                entryOutsideVolume3s: outsideVolume,
                entryOutsideVolume5s: outsideVolume5s);

            position.HighestPrice = entrySignal.Price;
            position.AllowReentry = _cfgReentry;

            state.LastEntryTime = entrySignal.Time;
            state.LastEntryRatio = entryRatio;
            state.FirstEntryOutsideVolume = outsideVolume;
        }

        private void CheckReentry(Position position, TickData row, LoopState state, double prevHighestPrice)
        {
            DateTime currentTime = state.CurrentTime.Value;
            double currentPrice = state.CurrentPrice;

            if (currentPrice <= prevHighestPrice) return;

            double currentOutsideVolume = _outsideVolumeTracker.GetCurrentVolume();
            if (currentOutsideVolume <= position.EntryOutsideVolume3s) return;

            HandleReentry(position, currentTime, currentPrice);
        }

        private void HandleTrailingExit(ExitResult exitResult)
        {
            _positionManager.TrailingStopExit(
                exitTime: exitResult.ExitTime,
                exitPrice: exitResult.ExitPrice,
                exitRatio: exitResult.ExitRatio,
                exitLevel: exitResult.ExitLevel ?? "3min",
                exitReason: exitResult.ExitReason ?? "");
        }

        private void HandleExit(ExitResult exitResult, DateTime currentTime, LoopState state)
        {
            string exitType = exitResult.ExitType ?? "";

            if (exitType == "partial")
            {
                _positionManager.PartialExit(
                    exitTime: exitResult.ExitTime,
                    exitPrice: exitResult.ExitPrice,
                    exitReason: exitResult.ExitReason ?? "");
            }
            else
            {
                _positionManager.ClosePosition(
                    exitTime: exitResult.ExitTime,
                    exitPrice: exitResult.ExitPrice,
                    exitReason: exitResult.ExitReason ?? "");

                state.LastExitTime = exitResult.ExitTime;
                state.AskWall.Reset();
                state.ModeC.Reset();
                state.ModeD.Reset();
                state.ModeE.Reset();
                string reason = exitResult.ExitReason ?? "";
                _lastExitWasStopLoss = reason == "tick_stop_loss";
                if (_lastExitWasStopLoss)
                {
                    _stopLossCount++;
                }
                else if (_cfgStopLossResetOnWin)
                {
                    // Reset stop loss counter on winning trade if configured
                    _stopLossCount = 0;
                }
            }
        }

        private void HandleReentry(Position position, DateTime currentTime, double currentPrice)
        {
            double currentOutsideVolume = _outsideVolumeTracker.GetCurrentVolume();
            _positionManager.ReentryPosition(currentTime, currentPrice, currentOutsideVolume);
            position.RemainingRatio = 1.0;
            position.AllowReentry = false;
        }

        private void ClosePositionCompletely(Position position, ExitResult trailingResult, LoopState state)
        {
            double totalExitRatio = 0.0;
            double weightedPrice = 0.0;
            foreach (var exitItem in position.TrailingExits)
            {
                double price = Convert.ToDouble(exitItem["price"]);
                double ratio = Convert.ToDouble(exitItem["ratio"]);
                weightedPrice += price * ratio;
                totalExitRatio += ratio;
            }

            double avgExitPrice = totalExitRatio > 0
                ? weightedPrice / totalExitRatio
                : trailingResult.ExitPrice;

            string closeReason = _exitManager.ModeEEnabled
                ? "Mode E 分批出場全出"
                : _exitManager.ModeDEnabled
                    ? "Mode D 分批出場全出"
                    : _exitManager.ModeCEnabled
                        ? "Mode C 分批出場全出"
                        : "Trailing stop full exit";

            _positionManager.ClosePosition(
                exitTime: trailingResult.ExitTime,
                exitPrice: avgExitPrice,
                exitReason: closeReason);

            state.LastExitTime = trailingResult.ExitTime;
            state.AskWall.Reset();
            state.ModeC.Reset();
            state.ModeD.Reset();
            state.ModeE.Reset();
            _lastExitWasStopLoss = false;
        }

        private void ForceCloseAtMarketClose(LoopState state)
        {
            if (_positionManager.HasPosition())
            {
                double closePrice = state.CurrentPrice;
                DateTime closeTime = state.CurrentTime ?? DateTime.Now;

                _positionManager.ClosePosition(
                    exitTime: closeTime,
                    exitPrice: closePrice,
                    exitReason: "Market close");

                state.LastExitTime = closeTime;
                state.AskWall.Reset();
                state.ModeC.Reset();
                state.ModeD.Reset();
                state.ModeE.Reset();
                _lastExitWasStopLoss = false;
                System.Console.WriteLine($"[MARKET CLOSE] Time: {closeTime}, Price: {closePrice:F2}");
            }
        }

        private void AddMetricsToData(List<TickData> data, MetricsAccumulator metrics)
        {
            for (int i = 0; i < data.Count && i < metrics.DayHighGrowthRates.Count; i++)
            {
                data[i].DayHighGrowthRate = metrics.DayHighGrowthRates[i];
                data[i].BidAvgVolume = metrics.BidAvgVolumes[i];
                data[i].AskAvgVolume = metrics.AskAvgVolumes[i];
                data[i].BalanceRatio = metrics.BalanceRatios[i];
                data[i].DayHighBreakout = metrics.DayHighBreakouts[i];
                data[i].InsideOutsideRatio = metrics.InsideOutsideRatios[i];
                data[i].OutsideRatio = metrics.OutsideRatios[i];
                data[i].LargeOrderIoRatio = metrics.LargeOrderIoRatios[i];
                data[i].LargeOrderOutsideRatio = metrics.LargeOrderOutsideRatios[i];

                if (i < metrics.VolumeShrinkSignals.Count)
                    data[i].VolumeShrinkSignal = metrics.VolumeShrinkSignals[i];
                if (i < metrics.VwapDeviationSignals.Count)
                    data[i].VwapDeviationSignal = metrics.VwapDeviationSignals[i];
            }
        }

        // ===== Helpers =====

        private static bool GetBool(Dictionary<string, object> d, string key, bool def)
        {
            if (d.TryGetValue(key, out var v))
            {
                if (v is bool b) return b;
                if (bool.TryParse(v?.ToString(), out bool p)) return p;
            }
            return def;
        }

        private static double GetDouble(Dictionary<string, object> d, string key, double def)
        {
            if (d.TryGetValue(key, out var v))
            {
                if (v is double dv) return dv;
                if (v is int iv) return iv;
                if (v is long lv) return lv;
                if (double.TryParse(v?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double p)) return p;
            }
            return def;
        }

        private static int GetInt(Dictionary<string, object> d, string key, int def)
        {
            if (d.TryGetValue(key, out var v))
            {
                if (v is int i) return i;
                if (v is long l) return (int)l;
                if (v is double dv) return (int)dv;
                if (int.TryParse(v?.ToString(), out int p)) return p;
            }
            return def;
        }

        private static TimeSpan GetTimeSpan(Dictionary<string, object> d, string key, TimeSpan def)
        {
            if (d.TryGetValue(key, out var v))
            {
                if (v is TimeSpan ts) return ts;
                if (v is DateTime dt) return dt.TimeOfDay;
                if (v is string s && TimeSpan.TryParse(s, out ts)) return ts;
            }
            return def;
        }
    }
}
