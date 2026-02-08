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
        private readonly MassiveMatchingTracker _massiveMatchingTracker;
        private readonly InsideOutsideRatioTracker _insideOutsideRatioTracker;
        private readonly InsideOutsideRatioTracker _largeOrderIoRatioTracker;

        // Configs
        private readonly Dictionary<string, object> _entryConfig;
        private readonly Dictionary<string, object> _exitConfig;
        private readonly Dictionary<string, object> _reentryConfig;

        private bool _lastExitWasStopLoss;

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
            _massiveMatchingTracker = engine.MassiveMatchingTracker;
            _insideOutsideRatioTracker = engine.InsideOutsideRatioTracker;
            _largeOrderIoRatioTracker = engine.LargeOrderIoRatioTracker;
            _entryConfig = engine.EntryConfig;
            _exitConfig = engine.ExitConfig;
            _reentryConfig = engine.ReentryConfig;
            _lastExitWasStopLoss = false;
        }

        /// <summary>
        /// Run the backtest loop over all tick data.
        /// </summary>
        public List<TradeRecord> Run(List<TickData> data, string stockId, double refPrice, double limitUpPrice)
        {
            var state = InitLoopState();
            var metrics = new MetricsAccumulator();

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

                // Only update prev_day_high for trade data
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
            return new LoopState();
        }

        private void UpdateCurrentState(TickData row, LoopState state)
        {
            state.CurrentTime = row.Time;
            state.CurrentPrice = row.Price;
            state.CurrentDayHigh = row.DayHigh > 0 ? row.DayHigh : row.Price;
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
        }

        private void ProcessExitLogic(LoopState state, TickData row, double limitUpPrice)
        {
            var position = _positionManager.GetCurrentPosition();
            DateTime currentTime = state.CurrentTime.Value;
            double currentPrice = state.CurrentPrice;

            double prevHighestPrice = position.HighestPrice;
            if (currentPrice > position.HighestPrice)
                position.HighestPrice = currentPrice;

            // Limit-up exit (priority)
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

                var protectionResult = _exitManager.CheckEntryPriceProtection(position, currentPrice, currentTime);
                if (protectionResult != null)
                {
                    HandleExit(protectionResult, currentTime, state);
                    return;
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
                && GetBool(_reentryConfig, "reentry", false)
                && position.AllowReentry
                && position.RemainingRatio < 1.0)
            {
                CheckReentry(position, row, state, prevHighestPrice);
            }
        }

        private void ProcessEntryLogic(TickData row, string stockId, double refPrice,
            double limitUpPrice, LoopState state, List<TickData> data)
        {
            DateTime currentTime = row.Time;
            int currentTickType = row.TickType;

            var waitingState = state.WaitingForOutsideEntry;
            var bufferState = state.BreakoutBuffer;

            bool isBreakout = _entryChecker.CheckDayHighBreakout(state.CurrentDayHigh, state.PrevDayHigh);

            // Check if in waiting-for-outside-entry state
            if (waitingState.Active)
            {
                bool isOutsideTick = (currentTickType == 1);
                bool differentTimestamp = (currentTime != waitingState.BreakoutTime);

                if (isOutsideTick && differentTimestamp)
                {
                    // Found qualifying outside tick
                    waitingState.Active = false;
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
                    return;
                }
            }
            else if (isBreakout)
            {
                state.DayHighBreakCount++;

                // Enter waiting state
                waitingState.Active = true;
                waitingState.BreakoutTime = currentTime;
                waitingState.BreakoutDayHigh = state.CurrentDayHigh;
                waitingState.PrevDayHigh = state.PrevDayHigh;

                // Traditional buffer mechanism
                if (GetBool(_entryConfig, "entry_buffer_enabled", false))
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

            if (GetBool(_entryConfig, "ratio_increase_after_loss_enabled", false) && _lastExitWasStopLoss)
            {
                double lastEntryRatio = state.LastEntryRatio;
                double defaultThreshold = GetDouble(_entryConfig, "ratio_entry_threshold", 3.0);
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

            // Check entry conditions
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
            if (GetBool(_entryConfig, "entry_buffer_enabled", false) && bufferState != null)
            {
                var startTime = bufferState.StartTime;
                int bufferMs = GetInt(_entryConfig, "entry_buffer_milliseconds", 0);
                if (bufferState.Active && startTime.HasValue && bufferMs > 0)
                {
                    double deltaMs = (currentTime - startTime.Value).TotalMilliseconds;
                    if (deltaMs <= bufferMs)
                        bufferActive = true;
                    else
                    {
                        bufferState.Active = false;
                        bufferState.StartTime = null;
                    }
                }
            }

            bool effectiveBreakout = isBreakout || bufferActive;
            if (!effectiveBreakout) return null;

            // Time checks
            var entryStartTime = GetTimeSpan(_entryConfig, "entry_start_time", new TimeSpan(9, 9, 0));
            if (currentTime.TimeOfDay < entryStartTime) return null;

            var cutoffTime = GetTimeSpan(_entryConfig, "entry_cutoff_time", new TimeSpan(13, 0, 0));
            if (currentTime.TimeOfDay >= cutoffTime) return null;

            // Build indicators dict
            var indicators = new Dictionary<string, double>
            {
                ["ratio"] = row.Ratio15s300s,
                ["pct_2min"] = row.Pct2Min,
                ["pct_3min"] = row.Pct3Min,
                ["pct_5min"] = row.Pct5Min,
                ["low_1m"] = row.Low1m > 0 ? row.Low1m : row.Price,
                ["low_3m"] = row.Low3m > 0 ? row.Low3m : row.Price,
                ["low_5m"] = row.Low5m > 0 ? row.Low5m : row.Price,
                ["low_3min"] = row.Low3m > 0 ? row.Low3m : row.Price,
                ["low_10min"] = row.Low10m > 0 ? row.Low10m : row.Price,
                ["low_15min"] = row.Low15m > 0 ? row.Low15m : row.Price
            };

            // Resolve bid/ask ratio
            double askBidRatio;
            double currentBidAskRatio = row.BidAskRatio;
            if (!double.IsNaN(currentBidAskRatio) && currentBidAskRatio > 0)
            {
                askBidRatio = currentBidAskRatio;
            }
            else if (data != null)
            {
                // Look for same-timestamp rows with valid ratios
                var sameTimeValid = data
                    .Where(t => t.Time == currentTime && !double.IsNaN(t.BidAskRatio) && t.BidAskRatio > 0)
                    .FirstOrDefault();
                askBidRatio = sameTimeValid != null
                    ? sameTimeValid.BidAskRatio
                    : (lastBidAskRatio ?? 0.0);
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

            var position = _positionManager.OpenPosition(
                entryTime: entrySignal.Time,
                entryPrice: entrySignal.Price,
                entryBidThickness: bidThickness,
                dayHighAtEntry: state.CurrentDayHigh,
                entryRatio: entryRatio,
                entryOutsideVolume3s: outsideVolume);

            position.HighestPrice = entrySignal.Price;
            position.AllowReentry = GetBool(_reentryConfig, "reentry", false);

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
                exitTime: (DateTime)exitResult["exit_time"],
                exitPrice: Convert.ToDouble(exitResult["exit_price"]),
                exitRatio: Convert.ToDouble(exitResult.GetValueOrDefault("exit_ratio", 0.5)),
                exitLevel: exitResult.GetValueOrDefault("exit_level", "3min")?.ToString() ?? "3min",
                exitReason: exitResult.GetValueOrDefault("exit_reason", "")?.ToString() ?? "");
        }

        private void HandleExit(ExitResult exitResult, DateTime currentTime, LoopState state)
        {
            string exitType = exitResult["exit_type"]?.ToString() ?? "";

            if (exitType == "partial")
            {
                _positionManager.PartialExit(
                    exitTime: (DateTime)exitResult["exit_time"],
                    exitPrice: Convert.ToDouble(exitResult["exit_price"]),
                    exitReason: exitResult["exit_reason"]?.ToString() ?? "");
            }
            else
            {
                _positionManager.ClosePosition(
                    exitTime: (DateTime)exitResult["exit_time"],
                    exitPrice: Convert.ToDouble(exitResult["exit_price"]),
                    exitReason: exitResult["exit_reason"]?.ToString() ?? "");

                state.LastExitTime = (DateTime)exitResult["exit_time"];
                _lastExitWasStopLoss = exitResult.GetValueOrDefault("exit_reason", "")?.ToString() == "tick_stop_loss";
            }
        }

        private void HandleReentry(Position position, DateTime currentTime, double currentPrice)
        {
            position.ReentryTime = currentTime;
            position.ReentryPrice = currentPrice;
            position.RemainingRatio = 1.0;
            position.AllowReentry = false;
            Console.WriteLine($"[REENTRY] Time: {currentTime}, Price: {currentPrice:F2}");
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
                : Convert.ToDouble(trailingResult["exit_price"]);

            _positionManager.ClosePosition(
                exitTime: (DateTime)trailingResult["exit_time"],
                exitPrice: avgExitPrice,
                exitReason: "Trailing stop full exit");

            state.LastExitTime = (DateTime)trailingResult["exit_time"];
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
                _lastExitWasStopLoss = false;
                Console.WriteLine($"[MARKET CLOSE] Time: {closeTime}, Price: {closePrice:F2}");
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
