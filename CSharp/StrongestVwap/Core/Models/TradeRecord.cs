using System;
using System.Collections.Generic;

namespace StrongestVwap.Core.Models
{
    /// <summary>
    /// A single take-profit limit order (1 of 5 splits).
    /// </summary>
    public class TakeProfitOrder
    {
        public int Index { get; set; }           // 0-4
        public double TargetPrice { get; set; }
        public double Shares { get; set; }
        public bool Filled { get; set; }
        public DateTime? FillTime { get; set; }
        public string Type { get; set; } = "takeProfit";  // "takeProfit" or "limitUp"
    }

    /// <summary>
    /// Trailing low exit state: tracks partial exits via low_10m / low_15m.
    /// Stage 0 = not triggered, Stage 1 = low_10m exited half, Stage 2 = low_15m exited rest.
    /// </summary>
    public class TrailingLowState
    {
        public int CurrentStage { get; set; } // 0, 1, 2
        public double Low10mExitShares { get; set; }
        public double Low15mExitShares { get; set; }
        public DateTime? Low10mExitTime { get; set; }
        public DateTime? Low15mExitTime { get; set; }
        public double? Low10mExitPrice { get; set; }
        public double? Low15mExitPrice { get; set; }

        public void Reset()
        {
            CurrentStage = 0;
            Low10mExitShares = 0;
            Low15mExitShares = 0;
            Low10mExitTime = null;
            Low15mExitTime = null;
            Low10mExitPrice = null;
            Low15mExitPrice = null;
        }
    }

    /// <summary>
    /// Rolling low 3-stage exit state: tracks partial exits via configurable low fields (e.g. low_1m/3m/5m).
    /// Stage 0 = not triggered, Stage 1 = field1 exited 1/3, Stage 2 = field2 exited 1/3, Stage 3 = field3 exited rest.
    /// </summary>
    public class RollingLowState
    {
        public int CurrentStage { get; set; } // 0, 1, 2, 3

        public double Stage1ExitShares { get; set; }
        public double Stage2ExitShares { get; set; }
        public double Stage3ExitShares { get; set; }

        public double? Stage1ExitPrice { get; set; }
        public double? Stage2ExitPrice { get; set; }
        public double? Stage3ExitPrice { get; set; }

        public DateTime? Stage1ExitTime { get; set; }
        public DateTime? Stage2ExitTime { get; set; }
        public DateTime? Stage3ExitTime { get; set; }

        public void Reset()
        {
            CurrentStage = 0;
            Stage1ExitShares = 0; Stage2ExitShares = 0; Stage3ExitShares = 0;
            Stage1ExitPrice = null; Stage2ExitPrice = null; Stage3ExitPrice = null;
            Stage1ExitTime = null; Stage2ExitTime = null; Stage3ExitTime = null;
        }
    }

    /// <summary>
    /// A complete trade from entry to full exit.
    /// </summary>
    public class TradeRecord
    {
        // Entry
        public string StockId { get; set; } = "";
        public DateTime EntryTime { get; set; }
        public double EntryPrice { get; set; }
        public double EntryVwap { get; set; }        // stop-loss base
        public double EntryDayHigh { get; set; }     // take-profit base
        public double TotalShares { get; set; }
        public double PositionCash { get; set; }

        // StrongGroup info at entry
        public string EntryGroupName { get; set; } = "";
        public int EntryGroupRank { get; set; }
        public int EntryTotalMemberRank { get; set; }
        public int EntryMemberRank { get; set; }
        public string EntryGroupMembers { get; set; } = "";

        // Take-profit orders (5 splits)
        public List<TakeProfitOrder> TakeProfitOrders { get; set; } = new();
        public bool ProfitTaken { get; set; }        // any TP order filled

        // Remaining shares (after partial TP fills)
        public double RemainingShares { get; set; }

        // Exit
        public DateTime? ExitTime { get; set; }
        public double? ExitPrice { get; set; }
        public string ExitReason { get; set; } = "";  // stopLoss, timeExit, takeProfit, bailout
        public bool IsFullyClosed { get; set; }

        // Trailing low exit state
        public TrailingLowState TrailingLow { get; set; } = new();

        // Rolling low 3-stage exit state
        public RollingLowState RollingLow { get; set; } = new();

        // PnL
        public double? PnlAmount { get; set; }
        public double? PnlPercent { get; set; }

        /// <summary>
        /// Calculate PnL based on all filled orders and final exit.
        /// </summary>
        public void CalculatePnl()
        {
            if (EntryPrice <= 0 || TotalShares <= 0) return;

            double totalProceeds = 0;
            double totalSharesSold = 0;

            // Sum proceeds from take-profit fills
            foreach (var tp in TakeProfitOrders)
            {
                if (tp.Filled)
                {
                    totalProceeds += tp.TargetPrice * tp.Shares;
                    totalSharesSold += tp.Shares;
                }
            }

            // Sum proceeds from trailing low partial exits
            if (TrailingLow.Low10mExitShares > 0 && TrailingLow.Low10mExitPrice.HasValue)
            {
                totalProceeds += TrailingLow.Low10mExitPrice.Value * TrailingLow.Low10mExitShares;
                totalSharesSold += TrailingLow.Low10mExitShares;
            }
            if (TrailingLow.Low15mExitShares > 0 && TrailingLow.Low15mExitPrice.HasValue)
            {
                totalProceeds += TrailingLow.Low15mExitPrice.Value * TrailingLow.Low15mExitShares;
                totalSharesSold += TrailingLow.Low15mExitShares;
            }

            // Sum proceeds from rolling low partial exits
            if (RollingLow.Stage1ExitShares > 0 && RollingLow.Stage1ExitPrice.HasValue)
            {
                totalProceeds += RollingLow.Stage1ExitPrice.Value * RollingLow.Stage1ExitShares;
                totalSharesSold += RollingLow.Stage1ExitShares;
            }
            if (RollingLow.Stage2ExitShares > 0 && RollingLow.Stage2ExitPrice.HasValue)
            {
                totalProceeds += RollingLow.Stage2ExitPrice.Value * RollingLow.Stage2ExitShares;
                totalSharesSold += RollingLow.Stage2ExitShares;
            }
            if (RollingLow.Stage3ExitShares > 0 && RollingLow.Stage3ExitPrice.HasValue)
            {
                totalProceeds += RollingLow.Stage3ExitPrice.Value * RollingLow.Stage3ExitShares;
                totalSharesSold += RollingLow.Stage3ExitShares;
            }

            // Add remaining shares sold at exit price
            if (RemainingShares > 0 && ExitPrice.HasValue)
            {
                totalProceeds += ExitPrice.Value * RemainingShares;
                totalSharesSold += RemainingShares;
            }

            double totalCost = EntryPrice * TotalShares;
            PnlAmount = totalProceeds - totalCost;
            PnlPercent = totalCost > 0 ? PnlAmount / totalCost * 100.0 : 0;
        }

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>
            {
                ["stock_id"] = StockId,
                ["entry_time"] = EntryTime,
                ["entry_price"] = EntryPrice,
                ["entry_vwap"] = EntryVwap,
                ["entry_day_high"] = EntryDayHigh,
                ["total_shares"] = TotalShares,
                ["position_cash"] = PositionCash,
                ["group_name"] = EntryGroupName,
                ["group_rank"] = EntryGroupRank,
                ["total_member_rank"] = EntryTotalMemberRank,
                ["member_rank"] = EntryMemberRank,
                ["profit_taken"] = ProfitTaken,
                ["exit_time"] = (object?)ExitTime ?? "",
                ["exit_price"] = (object?)ExitPrice ?? "",
                ["exit_reason"] = ExitReason,
                ["pnl_amount"] = (object?)PnlAmount ?? "",
                ["pnl_percent"] = (object?)PnlPercent ?? "",
                ["tp_fills"] = GetTpFillSummary()
            };
        }

        private string GetTpFillSummary()
        {
            var fills = new List<string>();
            foreach (var tp in TakeProfitOrders)
            {
                if (tp.Filled)
                    fills.Add($"TP{tp.Index + 1}@{tp.TargetPrice:F2}");
            }
            if (TrailingLow.Low10mExitShares > 0)
                fills.Add($"TL10m@{TrailingLow.Low10mExitPrice:F2}");
            if (TrailingLow.Low15mExitShares > 0)
                fills.Add($"TL15m@{TrailingLow.Low15mExitPrice:F2}");
            if (RollingLow.Stage1ExitShares > 0)
                fills.Add($"RL1@{RollingLow.Stage1ExitPrice:F2}");
            if (RollingLow.Stage2ExitShares > 0)
                fills.Add($"RL2@{RollingLow.Stage2ExitPrice:F2}");
            if (RollingLow.Stage3ExitShares > 0)
                fills.Add($"RL3@{RollingLow.Stage3ExitPrice:F2}");
            return fills.Count > 0 ? string.Join(",", fills) : "none";
        }
    }
}
