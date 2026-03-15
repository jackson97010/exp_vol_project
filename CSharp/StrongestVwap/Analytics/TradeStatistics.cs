using System;
using System.Collections.Generic;
using System.Linq;
using StrongestVwap.Core.Models;

namespace StrongestVwap.Analytics
{
    public static class TradeStatistics
    {
        public static void PrintSummary(List<TradeRecord> trades)
        {
            if (trades.Count == 0)
            {
                Console.WriteLine("[STATS] No trades.");
                return;
            }

            int total = trades.Count;
            int wins = trades.Count(t => t.PnlPercent.HasValue && t.PnlPercent > 0);
            int losses = trades.Count(t => t.PnlPercent.HasValue && t.PnlPercent <= 0);
            double winRate = total > 0 ? (double)wins / total * 100 : 0;

            double totalPnl = trades.Sum(t => t.PnlAmount ?? 0);
            double avgPnlPct = trades.Average(t => t.PnlPercent ?? 0);
            double maxWin = trades.Max(t => t.PnlPercent ?? 0);
            double maxLoss = trades.Min(t => t.PnlPercent ?? 0);

            // By exit reason
            var byReason = trades.GroupBy(t => t.ExitReason)
                .Select(g => new { Reason = g.Key, Count = g.Count(), AvgPnl = g.Average(t => t.PnlPercent ?? 0) })
                .OrderByDescending(g => g.Count);

            Console.WriteLine($"\n--- Trade Summary ---");
            Console.WriteLine($"Total trades:  {total}");
            Console.WriteLine($"Wins/Losses:   {wins}/{losses}  (WinRate: {winRate:F1}%)");
            Console.WriteLine($"Total PnL:     {totalPnl:N0}");
            Console.WriteLine($"Avg PnL %:     {avgPnlPct:F2}%");
            Console.WriteLine($"Max Win %:     {maxWin:F2}%");
            Console.WriteLine($"Max Loss %:    {maxLoss:F2}%");

            Console.WriteLine($"\nBy Exit Reason:");
            foreach (var r in byReason)
            {
                Console.WriteLine($"  {r.Reason,-15} count={r.Count,4}  avgPnl={r.AvgPnl:F2}%");
            }

            // Profit factor
            double grossWin = trades.Where(t => (t.PnlAmount ?? 0) > 0).Sum(t => t.PnlAmount ?? 0);
            double grossLoss = Math.Abs(trades.Where(t => (t.PnlAmount ?? 0) < 0).Sum(t => t.PnlAmount ?? 0));
            double profitFactor = grossLoss > 0 ? grossWin / grossLoss : double.PositiveInfinity;
            Console.WriteLine($"\nProfit Factor: {profitFactor:F2}");
            Console.WriteLine($"Gross Win:     {grossWin:N0}");
            Console.WriteLine($"Gross Loss:    {grossLoss:N0}");
            Console.WriteLine();
        }
    }
}
