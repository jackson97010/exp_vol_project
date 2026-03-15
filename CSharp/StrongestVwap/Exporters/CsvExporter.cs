using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using StrongestVwap.Core.Models;

namespace StrongestVwap.Exporters
{
    public static class CsvExporter
    {
        public static void ExportTrades(List<TradeRecord> trades, string path)
        {
            if (trades.Count == 0) return;

            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var sb = new StringBuilder();

                // Header
                sb.AppendLine("stock_id,entry_time,entry_price,entry_vwap,entry_day_high," +
                    "total_shares,position_cash,group_name,group_rank,total_member_rank,member_rank," +
                    "exit_time,exit_price,exit_reason,profit_taken,tp_fills," +
                    "pnl_amount,pnl_percent");

                foreach (var t in trades)
                {
                    sb.Append(t.StockId).Append(',');
                    sb.Append(t.EntryTime.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(',');
                    sb.Append(t.EntryPrice.ToString("F2", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(t.EntryVwap.ToString("F2", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(t.EntryDayHigh.ToString("F2", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(t.TotalShares.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(t.PositionCash.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(EscapeCsv(t.EntryGroupName)).Append(',');
                    sb.Append(t.EntryGroupRank).Append(',');
                    sb.Append(t.EntryTotalMemberRank).Append(',');
                    sb.Append(t.EntryMemberRank).Append(',');
                    sb.Append(t.ExitTime?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "").Append(',');
                    sb.Append(t.ExitPrice?.ToString("F2", CultureInfo.InvariantCulture) ?? "").Append(',');
                    sb.Append(t.ExitReason).Append(',');
                    sb.Append(t.ProfitTaken).Append(',');
                    sb.Append(GetTpFillSummary(t)).Append(',');
                    sb.Append(t.PnlAmount?.ToString("F2", CultureInfo.InvariantCulture) ?? "").Append(',');
                    sb.AppendLine(t.PnlPercent?.ToString("F4", CultureInfo.InvariantCulture) ?? "");
                }

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                Console.WriteLine($"[CSV] Exported {trades.Count} trades to {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to export CSV: {ex.Message}");
            }
        }

        private static string GetTpFillSummary(TradeRecord t)
        {
            var fills = new List<string>();
            foreach (var tp in t.TakeProfitOrders)
            {
                if (tp.Filled)
                    fills.Add($"TP{tp.Index + 1}@{tp.TargetPrice:F2}");
            }
            return fills.Count > 0 ? string.Join(";", fills) : "none";
        }

        /// <summary>
        /// Export trades as per-stock CSV files: {stockId}_trade_details_{date}.csv
        /// </summary>
        public static void ExportPerStockTrades(List<TradeRecord> trades, string dateOutputDir, string date)
        {
            if (trades.Count == 0) return;

            try
            {
                Directory.CreateDirectory(dateOutputDir);

                foreach (var t in trades)
                {
                    string fileName = $"{t.StockId}_trade_details_{date}.csv";
                    string filePath = Path.Combine(dateOutputDir, fileName);

                    var sb = new StringBuilder();
                    sb.Append('\uFEFF'); // UTF-8 BOM
                    sb.AppendLine("trade_no,entry_time,entry_price,entry_ratio,day_high_at_entry," +
                        "exit_time,exit_price,exit_reason,pnl_percent,group_name,group_rank,total_member_rank,member_rank,tp_fills");

                    double entryRatio = t.EntryPrice > 0 && t.EntryVwap > 0
                        ? (t.EntryPrice / t.EntryVwap - 1) * 100.0
                        : 0;

                    sb.Append("1,");
                    sb.Append(t.EntryTime.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(',');
                    sb.Append(t.EntryPrice.ToString("F2", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(entryRatio.ToString("F2", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(t.EntryDayHigh.ToString("F2", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(t.ExitTime?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "").Append(',');
                    sb.Append(t.ExitPrice?.ToString("F2", CultureInfo.InvariantCulture) ?? "").Append(',');
                    sb.Append(EscapeCsv(t.ExitReason)).Append(',');
                    sb.Append(t.PnlPercent?.ToString("F4", CultureInfo.InvariantCulture) ?? "").Append(',');
                    sb.Append(EscapeCsv(t.EntryGroupName)).Append(',');
                    sb.Append(t.EntryGroupRank).Append(',');
                    sb.Append(t.EntryTotalMemberRank).Append(',');
                    sb.Append(t.EntryMemberRank).Append(',');
                    sb.AppendLine(GetTpFillSummary(t));

                    File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
                }

                Console.WriteLine($"[CSV] Exported {trades.Count} per-stock CSVs to {dateOutputDir}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to export per-stock CSV: {ex.Message}");
            }
        }

        private static string EscapeCsv(string value)
        {
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
                return $"\"{value.Replace("\"", "\"\"")}\"";
            return value;
        }
    }
}
