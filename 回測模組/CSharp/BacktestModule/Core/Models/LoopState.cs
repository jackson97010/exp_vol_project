using System;

namespace BacktestModule.Core.Models
{
    /// <summary>
    /// State maintained across the tick-by-tick backtest loop.
    /// </summary>
    public class LoopState
    {
        public double PrevDayHigh { get; set; }
        public DateTime? LastEntryTime { get; set; }
        public DateTime? LastExitTime { get; set; }
        public int DayHighBreakCount { get; set; }
        public int DayHighBreakCountAfterEntryTime { get; set; }
        public double? LastBidAskRatio { get; set; }
        public double LastEntryRatio { get; set; }
        public double? DynamicRatioThreshold { get; set; }
        public double? FixedRatioThreshold { get; set; }
        public double? FirstEntryOutsideVolume { get; set; }

        // Current tick state
        public DateTime? CurrentTime { get; set; }
        public double CurrentPrice { get; set; }
        public double CurrentDayHigh { get; set; }
        public int CurrentTickType { get; set; }
        public double CurrentVolume { get; set; }

        // Breakout buffer state
        public BreakoutBufferState BreakoutBuffer { get; set; } = new BreakoutBufferState();

        // Waiting for outside entry state machine
        public WaitingForOutsideEntryState WaitingForOutsideEntry { get; set; } = new WaitingForOutsideEntryState();

        // Reentry buffer state
        public ReentryBufferState ReentryBuffer { get; set; } = new ReentryBufferState();
    }

    /// <summary>
    /// State for the entry buffer mechanism after Day High breakout.
    /// </summary>
    public class BreakoutBufferState
    {
        public bool Active { get; set; }
        public DateTime? StartTime { get; set; }
        public double DayHigh { get; set; }
        public bool Checked { get; set; }
    }

    /// <summary>
    /// State machine for waiting for an outside tick after breakout detection.
    /// </summary>
    public class WaitingForOutsideEntryState
    {
        public bool Active { get; set; }
        public DateTime? BreakoutTime { get; set; }
        public double BreakoutDayHigh { get; set; }
        public double PrevDayHigh { get; set; }
    }

    /// <summary>
    /// State for the reentry buffer mechanism.
    /// </summary>
    public class ReentryBufferState
    {
        public bool Active { get; set; }
        public DateTime? StartTime { get; set; }
        public double DayHigh { get; set; }
        public bool Checked { get; set; }
        public double MaxOutsideVolume { get; set; }
    }
}
