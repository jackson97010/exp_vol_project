using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using StrongestVwap.Core.Models;

namespace StrongestVwap.Exporters
{
    public static class ParquetExporter
    {
        public static void ExportTrades(List<TradeRecord> trades, string path)
        {
            Task.Run(async () => await ExportTradesAsync(trades, path)).GetAwaiter().GetResult();
        }

        private static async Task ExportTradesAsync(List<TradeRecord> trades, string path)
        {
            if (trades.Count == 0) return;

            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var fields = new DataField[]
                {
                    new DataField("stock_id", typeof(string)),
                    new DataField("entry_time", typeof(DateTime)),
                    new DataField("entry_price", typeof(double)),
                    new DataField("entry_vwap", typeof(double)),
                    new DataField("entry_day_high", typeof(double)),
                    new DataField("total_shares", typeof(double)),
                    new DataField("position_cash", typeof(double)),
                    new DataField("group_name", typeof(string)),
                    new DataField("group_rank", typeof(int)),
                    new DataField("total_member_rank", typeof(int)),
                    new DataField("member_rank", typeof(int)),
                    new DataField("group_members", typeof(string)),
                    new DataField("exit_time", typeof(DateTime), true),
                    new DataField("exit_price", typeof(double), true),
                    new DataField("exit_reason", typeof(string)),
                    new DataField("profit_taken", typeof(bool)),
                    new DataField("tp_fills", typeof(string)),
                    new DataField("pnl_amount", typeof(double), true),
                    new DataField("pnl_percent", typeof(double), true),
                    new DataField("entry_ratio", typeof(double)),
                };

                var schema = new ParquetSchema(fields);

                int n = trades.Count;
                var stockIds = new string[n];
                var entryTimes = new DateTime[n];
                var entryPrices = new double[n];
                var entryVwaps = new double[n];
                var entryDayHighs = new double[n];
                var totalShares = new double[n];
                var positionCash = new double[n];
                var groupNames = new string[n];
                var groupRanks = new int[n];
                var totalMemberRanks = new int[n];
                var memberRanks = new int[n];
                var groupMembersArr = new string[n];
                var exitTimes = new DateTime?[n];
                var exitPrices = new double?[n];
                var exitReasons = new string[n];
                var profitTaken = new bool[n];
                var tpFills = new string[n];
                var pnlAmounts = new double?[n];
                var pnlPercents = new double?[n];
                var entryRatios = new double[n];

                for (int i = 0; i < n; i++)
                {
                    var t = trades[i];
                    stockIds[i] = t.StockId;
                    entryTimes[i] = t.EntryTime;
                    entryPrices[i] = t.EntryPrice;
                    entryVwaps[i] = t.EntryVwap;
                    entryDayHighs[i] = t.EntryDayHigh;
                    totalShares[i] = t.TotalShares;
                    positionCash[i] = t.PositionCash;
                    groupNames[i] = t.EntryGroupName;
                    groupRanks[i] = t.EntryGroupRank;
                    totalMemberRanks[i] = t.EntryTotalMemberRank;
                    memberRanks[i] = t.EntryMemberRank;
                    groupMembersArr[i] = t.EntryGroupMembers;
                    exitTimes[i] = t.ExitTime;
                    exitPrices[i] = t.ExitPrice;
                    exitReasons[i] = t.ExitReason;
                    profitTaken[i] = t.ProfitTaken;
                    tpFills[i] = GetTpFillSummary(t);
                    pnlAmounts[i] = t.PnlAmount;
                    pnlPercents[i] = t.PnlPercent;
                    entryRatios[i] = t.EntryPrice > 0 && t.EntryVwap > 0
                        ? (t.EntryPrice / t.EntryVwap - 1) * 100.0
                        : 0;
                }

                var columns = new DataColumn[]
                {
                    new DataColumn(fields[0], stockIds),
                    new DataColumn(fields[1], entryTimes),
                    new DataColumn(fields[2], entryPrices),
                    new DataColumn(fields[3], entryVwaps),
                    new DataColumn(fields[4], entryDayHighs),
                    new DataColumn(fields[5], totalShares),
                    new DataColumn(fields[6], positionCash),
                    new DataColumn(fields[7], groupNames),
                    new DataColumn(fields[8], groupRanks),
                    new DataColumn(fields[9], totalMemberRanks),
                    new DataColumn(fields[10], memberRanks),
                    new DataColumn(fields[11], groupMembersArr),
                    new DataColumn(fields[12], exitTimes),
                    new DataColumn(fields[13], exitPrices),
                    new DataColumn(fields[14], exitReasons),
                    new DataColumn(fields[15], profitTaken),
                    new DataColumn(fields[16], tpFills),
                    new DataColumn(fields[17], pnlAmounts),
                    new DataColumn(fields[18], pnlPercents),
                    new DataColumn(fields[19], entryRatios),
                };

                using var stream = File.Create(path);
                await stream.WriteSingleRowGroupParquetFileAsync(schema, columns);

                Console.WriteLine($"[PARQUET] Exported {trades.Count} trades to {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to export Parquet: {ex.Message}");
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
    }
}
