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
        public double RunningDayHigh { get; set; }  // Track the actual running maximum price of the day
        public int CurrentTickType { get; set; }
        public double CurrentVolume { get; set; }

        // Breakout buffer state
        public BreakoutBufferState BreakoutBuffer { get; set; } = new BreakoutBufferState();

        // Waiting for outside entry state machine
        public WaitingForOutsideEntryState WaitingForOutsideEntry { get; set; } = new WaitingForOutsideEntryState();

        // Reentry buffer state
        public ReentryBufferState ReentryBuffer { get; set; } = new ReentryBufferState();

        // Ask wall exit observation state
        public AskWallState AskWall { get; set; } = new AskWallState();

        // Mode C three-stage exit state
        public ModeCState ModeC { get; set; } = new ModeCState();

        // Mode D percentage stop-loss + minute-low staged exit state
        public ModeDState ModeD { get; set; } = new ModeDState();

        // Mode E percentage take-profit + 15min low safety net exit state
        public ModeEState ModeE { get; set; } = new ModeEState();

        // Massive matching threshold (resolved at run start)
        public double MassiveThreshold { get; set; }
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

    /// <summary>
    /// State for ask wall (大壓單) exit observation/confirmation.
    /// </summary>
    public class AskWallState
    {
        /// <summary>Whether currently in observation period.</summary>
        public bool Active { get; set; }

        /// <summary>Time when signal was first detected.</summary>
        public DateTime? TriggerTime { get; set; }

        /// <summary>Day high at the time of signal detection.</summary>
        public double TriggerDayHigh { get; set; }

        /// <summary>Whether this position has already triggered an ask wall exit (once per position).</summary>
        public bool Triggered { get; set; }

        public void Reset()
        {
            Active = false;
            TriggerTime = null;
            TriggerDayHigh = 0.0;
            Triggered = false;
        }
    }

    /// <summary>
    /// State for Mode D percentage stop-loss + minute-low staged exit.
    /// Stage 0: waiting for Stage1 trigger (stop-loss or stage1_field)
    /// Stage 1: waiting for Stage2 trigger (stop-loss or stage2_field)
    /// Stage 2: waiting for Stage3 trigger (entry price protection or stage3_field)
    /// Stage 3: all stages complete
    /// </summary>
    public class ModeDState
    {
        /// <summary>Current stage: 0=waiting S1, 1=waiting S2, 2=waiting S3, 3=done.</summary>
        public int CurrentStage { get; set; }

        public void Reset()
        {
            CurrentStage = 0;
        }
    }

    /// <summary>
    /// State for Mode E percentage take-profit + 15min low safety net.
    /// Target 1: price >= entryPrice × (1 + target1_pct/100) → exit 33.3%
    /// Target 2: price >= entryPrice × (1 + target2_pct/100) → exit 33.3%
    /// Target 3: price >= entryPrice × (1 + target3_pct/100) → exit remaining
    /// Safety net: price <= low_15m → exit all remaining at any stage
    /// Stop-loss: price <= entryPrice × (1 - stop_loss_pct/100) → full exit
    /// </summary>
    public class ModeEState
    {
        /// <summary>Bitmask of reached targets: bit0=T1, bit1=T2, bit2=T3.</summary>
        public bool Target1Reached { get; set; }
        public bool Target2Reached { get; set; }
        public bool Target3Reached { get; set; }

        public void Reset()
        {
            Target1Reached = false;
            Target2Reached = false;
            Target3Reached = false;
        }
    }

    /// <summary>
    /// State for Mode C three-stage exit (掛單停利).
    /// Stage 0: waiting for Stage1 trigger (ask wall OR low_1m fallback)
    /// Stage 1: waiting for Stage2 trigger (vwap_5m deviation)
    /// Stage 2: waiting for Stage3 trigger (low_3m)
    /// Stage 3: all stages complete
    /// </summary>
    public class ModeCState
    {
        /// <summary>Current stage: 0=waiting S1, 1=waiting S2, 2=waiting S3, 3=done.</summary>
        public int CurrentStage { get; set; }

        /// <summary>Whether ask wall triggered Stage1 (vs low_1m fallback).</summary>
        public bool AskWallTriggered { get; set; }

        /// <summary>Whether path has been decided (ask wall or fallback).</summary>
        public bool PathDecided { get; set; }

        public void Reset()
        {
            CurrentStage = 0;
            AskWallTriggered = false;
            PathDecided = false;
        }
    }
}
