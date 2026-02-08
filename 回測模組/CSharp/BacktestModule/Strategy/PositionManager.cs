using System;
using System.Collections.Generic;

namespace BacktestModule.Strategy
{
    /// <summary>
    /// Represents an open position with all tracking state.
    /// </summary>
    public class Position
    {
        // Basic info
        public DateTime EntryTime { get; set; }
        public double EntryPrice { get; set; }
        public double EntryBidThickness { get; set; }
        public double DayHighAtEntry { get; set; }
        public double EntryRatio { get; set; }
        public double EntryOutsideVolume3s { get; set; }

        // Price tracking
        public double HighestPrice { get; set; }

        // First stage partial exit (50%)
        public bool PartialExitDone { get; set; }
        public DateTime? PartialExitTime { get; set; }
        public double? PartialExitPrice { get; set; }

        // Reentry
        public bool PartialExitRecovered { get; set; }
        public DateTime? ReentryTime { get; set; }
        public double? ReentryPrice { get; set; }
        public double? ReentryStopPrice { get; set; }
        public double ReentryOutsideAmount { get; set; }
        public double ReentryOutsideVolume3s { get; set; }

        // Final exit
        public DateTime? FinalExitTime { get; set; }
        public double? FinalExitPrice { get; set; }

        // Stop-loss tracking
        public int BuyWeakStreak { get; set; }

        // Other
        public double ShareCapital { get; set; }

        // Trailing stop fields
        public List<Dictionary<string, object>> TrailingExits { get; set; } = new();
        public double RemainingRatio { get; set; } = 1.0;
        public Dictionary<string, bool> ExitLevelsTriggered { get; set; } = new()
        {
            ["1min"] = false,
            ["3min"] = false,
            ["5min"] = false
        };

        // Reentry permission
        public bool AllowReentry { get; set; }

        // Momentum stop for reentry
        public bool ReentryMomentumStopTriggered { get; set; }
        public DateTime? ReentryMomentumStopTime { get; set; }

        public Position(
            DateTime entryTime,
            double entryPrice,
            double entryBidThickness,
            double dayHighAtEntry,
            double entryRatio,
            double entryOutsideVolume3s = 0.0)
        {
            EntryTime = entryTime;
            EntryPrice = entryPrice;
            EntryBidThickness = entryBidThickness;
            DayHighAtEntry = dayHighAtEntry;
            EntryRatio = entryRatio;
            EntryOutsideVolume3s = entryOutsideVolume3s;
            HighestPrice = entryPrice;
        }
    }

    /// <summary>
    /// Completed trade record (from entry to full exit).
    /// </summary>
    public class TradeRecord
    {
        public DateTime EntryTime { get; set; }
        public double EntryPrice { get; set; }
        public double EntryBidThickness { get; set; }
        public double EntryRatio { get; set; }
        public double DayHighAtEntry { get; set; }
        public double EntryOutsideVolume3s { get; set; }

        public DateTime? PartialExitTime { get; set; }
        public double? PartialExitPrice { get; set; }
        public string PartialExitReason { get; set; } = "";

        public DateTime? ReentryTime { get; set; }
        public double? ReentryPrice { get; set; }
        public double? ReentryStopPrice { get; set; }
        public double ReentryOutsideVolume3s { get; set; }

        public DateTime? FinalExitTime { get; set; }
        public double? FinalExitPrice { get; set; }
        public string FinalExitReason { get; set; } = "";
        public string ReentryExitReason { get; set; }

        public double? PnlPercent { get; set; }

        // Trailing stop details
        public List<Dictionary<string, object>> TrailingExitDetails { get; set; } = new();
        public int TotalExits { get; set; }
        public double FinalRemainingRatio { get; set; } = 1.0;

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>
            {
                ["entry_time"] = EntryTime,
                ["entry_price"] = EntryPrice,
                ["entry_bid_thickness"] = EntryBidThickness,
                ["entry_ratio"] = EntryRatio,
                ["day_high_at_entry"] = DayHighAtEntry,
                ["entry_outside_volume_3s"] = EntryOutsideVolume3s,
                ["partial_exit_time"] = (object)PartialExitTime,
                ["partial_exit_price"] = (object)PartialExitPrice,
                ["partial_exit_reason"] = PartialExitReason,
                ["reentry_time"] = (object)ReentryTime,
                ["reentry_price"] = (object)ReentryPrice,
                ["reentry_stop_price"] = (object)ReentryStopPrice,
                ["reentry_outside_volume_3s"] = ReentryOutsideVolume3s,
                ["final_exit_time"] = (object)FinalExitTime,
                ["final_exit_price"] = (object)FinalExitPrice,
                ["final_exit_reason"] = FinalExitReason,
                ["reentry_exit_reason"] = ReentryExitReason,
                ["pnl_percent"] = (object)PnlPercent,
                ["trailing_exit_details"] = TrailingExitDetails,
                ["total_exits"] = TotalExits,
                ["final_remaining_ratio"] = FinalRemainingRatio
            };
        }
    }

    /// <summary>
    /// Manages the current open position and trade history.
    /// </summary>
    public class PositionManager
    {
        public Position CurrentPosition { get; private set; }
        public TradeRecord CurrentTradeRecord { get; private set; }
        public List<TradeRecord> TradeHistory { get; private set; } = new();

        public void Reset()
        {
            CurrentPosition = null;
            CurrentTradeRecord = null;
            TradeHistory = new List<TradeRecord>();
        }

        public Position OpenPosition(
            DateTime entryTime,
            double entryPrice,
            double entryBidThickness,
            double dayHighAtEntry,
            double entryRatio,
            double entryOutsideVolume3s = 0.0)
        {
            CurrentPosition = new Position(
                entryTime, entryPrice, entryBidThickness,
                dayHighAtEntry, entryRatio, entryOutsideVolume3s);

            CurrentTradeRecord = new TradeRecord
            {
                EntryTime = entryTime,
                EntryPrice = entryPrice,
                EntryBidThickness = entryBidThickness,
                EntryRatio = entryRatio,
                DayHighAtEntry = dayHighAtEntry,
                EntryOutsideVolume3s = entryOutsideVolume3s
            };

            Console.WriteLine(
                $"[OPEN] Time: {entryTime}, Price: {entryPrice:F2}, " +
                $"DayHigh: {dayHighAtEntry:F2}, Ratio: {entryRatio:F1}, " +
                $"3sOutside: {entryOutsideVolume3s / 1000000:F2}M");

            return CurrentPosition;
        }

        /// <summary>
        /// Partial exit (reduce 50%).
        /// </summary>
        public bool PartialExit(DateTime exitTime, double exitPrice, string exitReason)
        {
            if (CurrentPosition == null || CurrentPosition.PartialExitDone)
                return false;

            CurrentPosition.PartialExitDone = true;
            CurrentPosition.PartialExitTime = exitTime;
            CurrentPosition.PartialExitPrice = exitPrice;
            CurrentPosition.RemainingRatio = 0.5;

            if (CurrentTradeRecord != null)
            {
                CurrentTradeRecord.PartialExitTime = exitTime;
                CurrentTradeRecord.PartialExitPrice = exitPrice;
                CurrentTradeRecord.PartialExitReason = exitReason;
            }

            // Set reentry stop price: partial exit price - 1 tick
            CurrentPosition.ReentryStopPrice = TickSizeHelper.AddTicks(exitPrice, -1);

            Console.WriteLine(
                $"[PARTIAL EXIT] Time: {exitTime}, Price: {exitPrice:F2}, " +
                $"Reason: {exitReason}, ReentryStop: {CurrentPosition.ReentryStopPrice:F2}");

            return true;
        }

        /// <summary>
        /// Reentry position after partial exit.
        /// </summary>
        public bool ReentryPosition(DateTime reentryTime, double reentryPrice, double reentryOutsideVolume3s)
        {
            if (CurrentPosition == null || !CurrentPosition.PartialExitDone)
                return false;
            if (CurrentPosition.PartialExitRecovered)
                return false;

            CurrentPosition.PartialExitRecovered = true;
            CurrentPosition.ReentryTime = reentryTime;
            CurrentPosition.ReentryPrice = reentryPrice;
            CurrentPosition.ReentryOutsideAmount = 0.0;
            CurrentPosition.ReentryOutsideVolume3s = reentryOutsideVolume3s;

            // Update reentry stop price
            if (CurrentPosition.PartialExitPrice.HasValue)
            {
                CurrentPosition.ReentryStopPrice = TickSizeHelper.AddTicks(
                    CurrentPosition.PartialExitPrice.Value, -1);
            }

            if (CurrentTradeRecord != null)
            {
                CurrentTradeRecord.ReentryTime = reentryTime;
                CurrentTradeRecord.ReentryPrice = reentryPrice;
                CurrentTradeRecord.ReentryStopPrice = CurrentPosition.ReentryStopPrice;
                CurrentTradeRecord.ReentryOutsideVolume3s = reentryOutsideVolume3s;
            }

            Console.WriteLine(
                $"[REENTRY] Time: {reentryTime}, Price: {reentryPrice:F2}, " +
                $"3sOutside: {reentryOutsideVolume3s / 1000000:F2}M, " +
                $"StopPrice: {CurrentPosition.ReentryStopPrice:F2}");

            return true;
        }

        /// <summary>
        /// Trailing stop exit (partial position exit).
        /// </summary>
        public bool TrailingStopExit(
            DateTime exitTime, double exitPrice, double exitRatio,
            string exitLevel, string exitReason)
        {
            if (CurrentPosition == null)
                return false;

            CurrentPosition.RemainingRatio -= exitRatio;

            CurrentPosition.TrailingExits.Add(new Dictionary<string, object>
            {
                ["time"] = exitTime,
                ["price"] = exitPrice,
                ["ratio"] = exitRatio,
                ["level"] = exitLevel,
                ["reason"] = exitReason
            });

            if (CurrentPosition.ExitLevelsTriggered.ContainsKey(exitLevel))
                CurrentPosition.ExitLevelsTriggered[exitLevel] = true;

            if (CurrentTradeRecord != null)
            {
                CurrentTradeRecord.TrailingExitDetails.Add(new Dictionary<string, object>
                {
                    ["time"] = exitTime,
                    ["price"] = exitPrice,
                    ["ratio"] = exitRatio,
                    ["level"] = exitLevel,
                    ["reason"] = exitReason
                });
                CurrentTradeRecord.TotalExits++;
                CurrentTradeRecord.FinalRemainingRatio = CurrentPosition.RemainingRatio;
            }

            Console.WriteLine(
                $"[TRAILING STOP] Time: {exitTime}, Price: {exitPrice:F2}, " +
                $"Level: {exitLevel}, Ratio: {exitRatio:P1}, " +
                $"Remaining: {CurrentPosition.RemainingRatio:P1}, Reason: {exitReason}");

            return true;
        }

        /// <summary>
        /// Closes the position completely. Calculates PnL.
        /// </summary>
        public TradeRecord ClosePosition(
            DateTime exitTime, double exitPrice, string exitReason,
            bool isReentryExit = false)
        {
            if (CurrentPosition == null)
                return null;

            CurrentPosition.FinalExitTime = exitTime;
            CurrentPosition.FinalExitPrice = exitPrice;

            if (CurrentTradeRecord != null)
            {
                CurrentTradeRecord.FinalExitTime = exitTime;
                CurrentTradeRecord.FinalExitPrice = exitPrice;

                if (isReentryExit)
                    CurrentTradeRecord.ReentryExitReason = exitReason;
                else
                    CurrentTradeRecord.FinalExitReason = exitReason;

                double entryPrice = CurrentTradeRecord.EntryPrice;

                // PnL calculation
                if (CurrentPosition.TrailingExits.Count > 0)
                {
                    // Trailing stop mode: sum of weighted PnLs
                    double totalPnl = 0.0;
                    foreach (var exit in CurrentPosition.TrailingExits)
                    {
                        double ep = Convert.ToDouble(exit["price"]);
                        double er = Convert.ToDouble(exit["ratio"]);
                        totalPnl += (ep - entryPrice) / entryPrice * 100.0 * er;
                    }
                    // Add remaining position PnL
                    if (CurrentPosition.RemainingRatio > 0)
                    {
                        totalPnl += (exitPrice - entryPrice) / entryPrice * 100.0 * CurrentPosition.RemainingRatio;
                    }
                    CurrentTradeRecord.PnlPercent = totalPnl;
                }
                else if (CurrentTradeRecord.PartialExitPrice.HasValue)
                {
                    // Two-stage mode: 50% at partial + 50% at final
                    double profit1 = (CurrentTradeRecord.PartialExitPrice.Value - entryPrice) / entryPrice * 100.0 * 0.5;
                    double profit2 = (exitPrice - entryPrice) / entryPrice * 100.0 * 0.5;
                    CurrentTradeRecord.PnlPercent = profit1 + profit2;
                }
                else
                {
                    // Full exit
                    CurrentTradeRecord.PnlPercent = (exitPrice - entryPrice) / entryPrice * 100.0;
                }

                TradeHistory.Add(CurrentTradeRecord);

                Console.WriteLine(
                    $"[CLOSE] Time: {exitTime}, Price: {exitPrice:F2}, " +
                    $"Reason: {exitReason}, PnL: {CurrentTradeRecord.PnlPercent:F2}%");
            }

            var tradeRecord = CurrentTradeRecord;
            CurrentPosition = null;
            CurrentTradeRecord = null;
            return tradeRecord;
        }

        public bool HasPosition()
        {
            return CurrentPosition != null && CurrentPosition.RemainingRatio > 0;
        }

        public Position GetCurrentPosition()
        {
            return CurrentPosition;
        }

        public List<TradeRecord> GetTradeHistory()
        {
            return TradeHistory;
        }
    }

    /// <summary>
    /// Reentry Manager: checks conditions for re-entering a partially exited position.
    /// </summary>
    public class ReentryManager
    {
        private readonly Dictionary<string, object> _config;

        public ReentryManager(Dictionary<string, object> config)
        {
            _config = config;
        }

        /// <summary>
        /// Checks reentry conditions:
        /// 1. Partial exit done
        /// 2. Not already recovered
        /// 3. Price > highest_price
        /// 4. Current 3s outside volume > entry 3s outside volume
        /// </summary>
        public Dictionary<string, object> CheckReentryConditions(
            Core.Models.TickData currentRow,
            Position position,
            OutsideVolumeTracker outsideVolumeTracker)
        {
            var result = new Dictionary<string, object>
            {
                ["pass"] = false,
                ["conditions"] = new Dictionary<string, object>(),
                ["failure_reason"] = ""
            };

            double currentPrice = currentRow.Price;

            if (!position.PartialExitDone)
            {
                result["failure_reason"] = "Partial exit not done";
                return result;
            }

            if (position.PartialExitRecovered)
            {
                result["failure_reason"] = "Already recovered";
                return result;
            }

            if (currentPrice <= position.HighestPrice)
            {
                result["failure_reason"] = "Price not new high";
                return result;
            }

            double currentOutsideVolume = outsideVolumeTracker.GetVolume3s();
            double entryOutsideVolume = position.EntryOutsideVolume3s;

            if (currentOutsideVolume <= entryOutsideVolume)
            {
                result["failure_reason"] = "3s outside volume insufficient";
                return result;
            }

            result["pass"] = true;
            result["failure_reason"] = "";
            return result;
        }

        /// <summary>
        /// Checks reentry conditions using a specified override volume.
        /// </summary>
        public Dictionary<string, object> CheckReentryConditionsWithVolume(
            Core.Models.TickData currentRow,
            Position position,
            double overrideVolume)
        {
            var result = new Dictionary<string, object>
            {
                ["pass"] = false,
                ["conditions"] = new Dictionary<string, object>(),
                ["failure_reason"] = ""
            };

            double currentPrice = currentRow.Price;

            if (!position.PartialExitDone)
            {
                result["failure_reason"] = "Partial exit not done";
                return result;
            }

            if (position.PartialExitRecovered)
            {
                result["failure_reason"] = "Already recovered";
                return result;
            }

            if (currentPrice <= position.HighestPrice)
            {
                result["failure_reason"] = "Price not new high";
                return result;
            }

            if (overrideVolume <= position.EntryOutsideVolume3s)
            {
                result["failure_reason"] = "3s outside volume insufficient";
                return result;
            }

            result["pass"] = true;
            result["failure_reason"] = "";
            return result;
        }
    }
}
