using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;

namespace StrongestVwap.Strategy
{
    public class ScreeningRecord
    {
        public string Date { get; set; } = "";
        public string StockId { get; set; } = "";
        public string StockName { get; set; } = "";
        public string Category { get; set; } = "";
        public double AvgAmount20d { get; set; }
        public double TodayAmount { get; set; }
        public double GroupMa20Sum { get; set; }
        public double GroupTodaySum { get; set; }
        public double ValRatio { get; set; }
    }

    public class GroupScreeningResult
    {
        public string Date { get; set; } = "";
        public string GroupName { get; set; } = "";
        public int Rank { get; set; }
        public double AvgPriceChangePct { get; set; }
        public double GroupMa20Sum { get; set; }
        public double GroupTodaySum { get; set; }
        public double ValRatio { get; set; }
        public int ValidMemberCount { get; set; }
        public List<MemberScreeningResult> Members { get; set; } = new();
        public List<MemberScreeningResult> SelectedMembers { get; set; } = new();
    }

    public class MemberScreeningResult
    {
        public string StockId { get; set; } = "";
        public string StockName { get; set; } = "";
        public double PriceChangePct { get; set; }
        public double AvgAmount20d { get; set; }
        public double TodayAmount { get; set; }
        public int RankInGroup { get; set; }
        public bool IsSelected { get; set; }
    }

    public class OfflineScreener
    {
        private readonly StrategyConfig _config;

        // Thresholds from config
        private readonly double _memberMinMonthVal;
        private readonly double _groupMinMonthVal;
        private readonly double _groupMinAvgPctChg;
        private readonly double _groupMinValRatio;
        private readonly bool _isWeightedAvg;
        private readonly int _groupValidTopN;
        private readonly int _topGroupRankThreshold;
        private readonly int _topGroupMaxSelect;
        private readonly int _normalGroupMaxSelect;
        private readonly double _entryMinVwapPctChg;

        // Close price data: date -> stock_id -> close_price
        private Dictionary<string, Dictionary<string, double>> _closePrices = new();

        public OfflineScreener(StrategyConfig config)
        {
            _config = config;
            _memberMinMonthVal = config.GetDouble("member_min_month_trading_val", 200_000_000);
            _groupMinMonthVal = config.GetDouble("group_min_month_trading_val", 3_000_000_000);
            _groupMinAvgPctChg = config.GetDouble("group_min_avg_pct_chg", 0.01);
            _groupMinValRatio = config.GetDouble("group_min_val_ratio", 1.2);
            _isWeightedAvg = config.GetBool("is_weighted_avg", false);
            _groupValidTopN = config.GetInt("group_valid_top_n", 20);
            _topGroupRankThreshold = config.GetInt("top_group_rank_threshold", 10);
            _topGroupMaxSelect = config.GetInt("top_group_max_select", 1);
            _normalGroupMaxSelect = config.GetInt("normal_group_max_select", 1);
            _entryMinVwapPctChg = config.GetDouble("entry_min_vwap_pct_chg", 0.04);
        }

        public void LoadClosePrices(string closePricesPath)
        {
            if (closePricesPath.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                _closePrices = LoadCloseParquet(closePricesPath);
            else
                _closePrices = LoadCloseCsv(closePricesPath);

            Console.WriteLine($"[SCREENER] Loaded close prices: {_closePrices.Count} dates");
        }

        public List<ScreeningRecord> LoadScreeningCsv(string path)
        {
            var records = new List<ScreeningRecord>();

            if (!File.Exists(path))
            {
                Console.WriteLine($"[ERROR] Screening CSV not found: {path}");
                return records;
            }

            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0) return records;

            // Parse header (handle BOM)
            string header = lines[0].TrimStart('\uFEFF');
            var headers = header.Split(',').Select(h => h.Trim()).ToList();

            int dateIdx = FindHeaderIndex(headers, "date", "日期");
            int stockIdIdx = FindHeaderIndex(headers, "stock_id", "代碼", "code");
            int stockNameIdx = FindHeaderIndex(headers, "stock_name", "股票名稱", "name");
            int categoryIdx = FindHeaderIndex(headers, "category", "stock_category", "族群", "group");
            int avgAmount20dIdx = FindHeaderIndex(headers, "avg_amount_20d");
            int todayAmountIdx = FindHeaderIndex(headers, "today_amount");
            int groupMa20SumIdx = FindHeaderIndex(headers, "group_ma20_sum");
            int groupTodaySumIdx = FindHeaderIndex(headers, "group_today_sum");
            int valRatioIdx = FindHeaderIndex(headers, "val_ratio");

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var parts = lines[i].Split(',');

                var rec = new ScreeningRecord();
                if (dateIdx >= 0 && dateIdx < parts.Length) rec.Date = parts[dateIdx].Trim();
                if (stockIdIdx >= 0 && stockIdIdx < parts.Length) rec.StockId = parts[stockIdIdx].Trim();
                if (stockNameIdx >= 0 && stockNameIdx < parts.Length) rec.StockName = parts[stockNameIdx].Trim();
                if (categoryIdx >= 0 && categoryIdx < parts.Length) rec.Category = parts[categoryIdx].Trim();
                if (avgAmount20dIdx >= 0 && avgAmount20dIdx < parts.Length &&
                    double.TryParse(parts[avgAmount20dIdx], NumberStyles.Any, CultureInfo.InvariantCulture, out var aVal))
                    rec.AvgAmount20d = aVal;
                if (todayAmountIdx >= 0 && todayAmountIdx < parts.Length &&
                    double.TryParse(parts[todayAmountIdx], NumberStyles.Any, CultureInfo.InvariantCulture, out var tVal))
                    rec.TodayAmount = tVal;
                if (groupMa20SumIdx >= 0 && groupMa20SumIdx < parts.Length &&
                    double.TryParse(parts[groupMa20SumIdx], NumberStyles.Any, CultureInfo.InvariantCulture, out var gVal))
                    rec.GroupMa20Sum = gVal;
                if (groupTodaySumIdx >= 0 && groupTodaySumIdx < parts.Length &&
                    double.TryParse(parts[groupTodaySumIdx], NumberStyles.Any, CultureInfo.InvariantCulture, out var gsVal))
                    rec.GroupTodaySum = gsVal;
                if (valRatioIdx >= 0 && valRatioIdx < parts.Length &&
                    double.TryParse(parts[valRatioIdx], NumberStyles.Any, CultureInfo.InvariantCulture, out var vVal))
                    rec.ValRatio = vVal;

                records.Add(rec);
            }

            Console.WriteLine($"[SCREENER] Loaded {records.Count} screening records from {path}");
            return records;
        }

        public List<GroupScreeningResult> RunScreening(List<ScreeningRecord> records, string targetDate)
        {
            // Filter records for the target date
            var dateRecords = records.Where(r => r.Date == targetDate).ToList();
            if (dateRecords.Count == 0)
            {
                Console.WriteLine($"[SCREENER] No records for date {targetDate}");
                return new List<GroupScreeningResult>();
            }

            // Get previous trading date's close prices for computing price change
            string prevDate = GetPreviousTradingDate(targetDate);
            if (prevDate == "")
            {
                Console.WriteLine($"[SCREENER] No previous trading date found for {targetDate}");
                return new List<GroupScreeningResult>();
            }

            Dictionary<string, double>? prevCloses = null;
            _closePrices.TryGetValue(prevDate, out prevCloses);

            Dictionary<string, double>? todayCloses = null;
            _closePrices.TryGetValue(targetDate, out todayCloses);

            if (prevCloses == null)
            {
                Console.WriteLine($"[SCREENER] No close prices for previous date {prevDate}");
                return new List<GroupScreeningResult>();
            }
            if (todayCloses == null)
            {
                Console.WriteLine($"[SCREENER] No close prices for date {targetDate}");
                return new List<GroupScreeningResult>();
            }

            // Group by category
            var grouped = dateRecords.GroupBy(r => r.Category);
            var validGroups = new List<GroupScreeningResult>();

            foreach (var group in grouped)
            {
                string groupName = group.Key;
                var members = group.ToList();

                double totalMonthVal = 0;
                double sumPctChg = 0;
                double sumWeight = 0;
                int validCount = 0;
                var memberResults = new List<MemberScreeningResult>();

                foreach (var m in members)
                {
                    // Check member min monthly value
                    if (m.AvgAmount20d < _memberMinMonthVal)
                        continue;

                    totalMonthVal += m.AvgAmount20d;

                    // Get price change
                    double prevClose = 0, todayClose = 0;
                    prevCloses.TryGetValue(m.StockId, out prevClose);
                    todayCloses.TryGetValue(m.StockId, out todayClose);

                    double priceChangePct = 0;
                    if (prevClose > 0 && todayClose > 0)
                        priceChangePct = (todayClose - prevClose) / prevClose;

                    var mr = new MemberScreeningResult
                    {
                        StockId = m.StockId,
                        StockName = m.StockName,
                        PriceChangePct = priceChangePct,
                        AvgAmount20d = m.AvgAmount20d,
                        TodayAmount = m.TodayAmount
                    };
                    memberResults.Add(mr);
                    validCount++;

                    if (_isWeightedAvg)
                    {
                        sumPctChg += priceChangePct * m.TodayAmount;
                        sumWeight += m.TodayAmount;
                    }
                    else
                    {
                        sumPctChg += priceChangePct;
                        sumWeight += 1;
                    }
                }

                double avgPctChg = sumWeight > 0 ? sumPctChg / sumWeight : 0;
                double valRatio = members.Count > 0 ? members[0].ValRatio : 0;
                double groupMa20Sum = members.Count > 0 ? members[0].GroupMa20Sum : 0;
                double groupTodaySum = members.Count > 0 ? members[0].GroupTodaySum : 0;

                // Recalculate val_ratio from group totals
                if (groupMa20Sum > 0)
                    valRatio = groupTodaySum / groupMa20Sum;

                // Check group validity
                bool isValid = totalMonthVal >= _groupMinMonthVal
                    && avgPctChg > _groupMinAvgPctChg
                    && valRatio >= _groupMinValRatio
                    && validCount > 0;

                if (!isValid) continue;

                // Rank members by price change (descending)
                memberResults = memberResults.OrderByDescending(m => m.PriceChangePct).ToList();
                for (int i = 0; i < memberResults.Count; i++)
                    memberResults[i].RankInGroup = i + 1;

                validGroups.Add(new GroupScreeningResult
                {
                    Date = targetDate,
                    GroupName = groupName,
                    AvgPriceChangePct = avgPctChg,
                    GroupMa20Sum = groupMa20Sum,
                    GroupTodaySum = groupTodaySum,
                    ValRatio = valRatio,
                    ValidMemberCount = validCount,
                    Members = memberResults
                });
            }

            // Rank groups by avg price change (descending)
            validGroups = validGroups.OrderByDescending(g => g.AvgPriceChangePct).ToList();
            for (int i = 0; i < validGroups.Count; i++)
                validGroups[i].Rank = i + 1;

            // Select members from top N groups
            foreach (var g in validGroups)
            {
                if (g.Rank > _groupValidTopN) continue;

                int maxSelect = g.Rank <= _topGroupRankThreshold
                    ? _topGroupMaxSelect
                    : _normalGroupMaxSelect;

                foreach (var m in g.Members)
                {
                    if (m.RankInGroup <= maxSelect && m.PriceChangePct >= _entryMinVwapPctChg)
                    {
                        m.IsSelected = true;
                        g.SelectedMembers.Add(m);
                    }
                }
            }

            return validGroups;
        }

        public void PrintResults(List<GroupScreeningResult> results)
        {
            if (results.Count == 0)
            {
                Console.WriteLine("[SCREENER] No valid groups.");
                return;
            }

            string date = results[0].Date;
            Console.WriteLine($"\n{'=',-80}");
            Console.WriteLine($" Group Screening Results for {date}");
            Console.WriteLine($" Weighted Avg: {_isWeightedAvg} | Min Avg Chg: {_groupMinAvgPctChg:P1} | " +
                $"Min Val Ratio: {_groupMinValRatio:F1} | Top N: {_groupValidTopN}");
            Console.WriteLine($"{'=',-80}");

            Console.WriteLine($"\n{"Rank",4} {"Group",-16} {"Avg Chg%",9} {"ValRatio",9} {"Members",8} {"Selected",9}  Selected Stocks");
            Console.WriteLine(new string('-', 90));

            foreach (var g in results.Where(g => g.Rank <= _groupValidTopN))
            {
                string selectedStocks = g.SelectedMembers.Count > 0
                    ? string.Join(", ", g.SelectedMembers.Select(m => $"{m.StockId}({m.StockName}) {m.PriceChangePct:P1}"))
                    : "-";

                Console.WriteLine($"{g.Rank,4} {g.GroupName,-16} {g.AvgPriceChangePct * 100,8:F2}% {g.ValRatio,9:F3} {g.ValidMemberCount,8} {g.SelectedMembers.Count,9}  {selectedStocks}");
            }

            // Summary
            int totalSelected = results.Sum(g => g.SelectedMembers.Count);
            Console.WriteLine($"\n  Valid groups: {results.Count} | Top {_groupValidTopN} groups shown");
            Console.WriteLine($"  Total selected stocks: {totalSelected}");

            if (totalSelected > 0)
            {
                Console.WriteLine($"\n  --- Selected Stock Details ---");
                Console.WriteLine($"  {"Stock",8} {"Name",-12} {"Group",-16} {"GRank",5} {"MRank",5} {"Chg%",8} {"Avg20d",14}");
                Console.WriteLine($"  {new string('-', 75)}");
                foreach (var g in results.Where(g => g.SelectedMembers.Count > 0))
                {
                    foreach (var m in g.SelectedMembers)
                    {
                        Console.WriteLine($"  {m.StockId,8} {m.StockName,-12} {g.GroupName,-16} {g.Rank,5} {m.RankInGroup,5} {m.PriceChangePct * 100,7:F2}% {m.AvgAmount20d,14:N0}");
                    }
                }
            }
        }

        public void ExportResultsCsv(List<GroupScreeningResult> results, string outputPath)
        {
            if (results.Count == 0) return;

            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine("date,group_rank,group_name,group_avg_chg_pct,group_val_ratio,group_member_count," +
                "stock_id,stock_name,member_rank,price_chg_pct,avg_amount_20d,today_amount,is_selected");

            foreach (var g in results)
            {
                foreach (var m in g.Members)
                {
                    sb.Append(g.Date).Append(',');
                    sb.Append(g.Rank).Append(',');
                    sb.Append(EscapeCsv(g.GroupName)).Append(',');
                    sb.Append((g.AvgPriceChangePct * 100).ToString("F4", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(g.ValRatio.ToString("F4", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(g.ValidMemberCount).Append(',');
                    sb.Append(m.StockId).Append(',');
                    sb.Append(EscapeCsv(m.StockName)).Append(',');
                    sb.Append(m.RankInGroup).Append(',');
                    sb.Append((m.PriceChangePct * 100).ToString("F4", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(m.AvgAmount20d.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(m.TodayAmount.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
                    sb.AppendLine(m.IsSelected ? "true" : "false");
                }
            }

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            Console.WriteLine($"[SCREENER] Exported to {outputPath}");
        }

        public List<string> GetAvailableDates(List<ScreeningRecord> records)
        {
            return records.Select(r => r.Date).Distinct().OrderBy(d => d).ToList();
        }

        #region Private helpers

        private string GetPreviousTradingDate(string date)
        {
            var sortedDates = _closePrices.Keys.OrderBy(d => d).ToList();
            int idx = sortedDates.IndexOf(date);
            if (idx <= 0)
            {
                // Try to find the closest date before target
                for (int i = sortedDates.Count - 1; i >= 0; i--)
                {
                    if (string.Compare(sortedDates[i], date, StringComparison.Ordinal) < 0)
                        return sortedDates[i];
                }
                return "";
            }
            return sortedDates[idx - 1];
        }

        private static int FindHeaderIndex(List<string> headers, params string[] candidates)
        {
            foreach (var c in candidates)
            {
                for (int i = 0; i < headers.Count; i++)
                {
                    if (headers[i].Equals(c, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            return -1;
        }

        private static string EscapeCsv(string value)
        {
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
                return $"\"{value.Replace("\"", "\"\"")}\"";
            return value;
        }

        private static Dictionary<string, Dictionary<string, double>> LoadCloseParquet(string path)
        {
            return Task.Run(async () => await LoadCloseParquetAsync(path)).GetAwaiter().GetResult();
        }

        private static async Task<Dictionary<string, Dictionary<string, double>>> LoadCloseParquetAsync(string path)
        {
            var result = new Dictionary<string, Dictionary<string, double>>();

            if (!File.Exists(path))
            {
                Console.WriteLine($"[ERROR] Close prices file not found: {path}");
                return result;
            }

            try
            {
                using var stream = File.OpenRead(path);
                using var reader = await ParquetReader.CreateAsync(stream);
                var fields = reader.Schema.GetDataFields();
                var fieldNames = fields.Select(f => f.Name).ToList();

                // Find date column
                int dateColIdx = -1;
                for (int i = 0; i < fieldNames.Count; i++)
                {
                    string fn = fieldNames[i].ToLowerInvariant();
                    if (fn == "date" || fn == "__index_level_0__" || fn == "index")
                    {
                        dateColIdx = i;
                        break;
                    }
                }

                // Stock columns = all columns except date
                var stockColumns = new List<(int idx, string stockId)>();
                for (int i = 0; i < fieldNames.Count; i++)
                {
                    if (i == dateColIdx) continue;
                    stockColumns.Add((i, fieldNames[i]));
                }

                for (int rg = 0; rg < reader.RowGroupCount; rg++)
                {
                    using var rgReader = reader.OpenRowGroupReader(rg);
                    var columns = new DataColumn[fields.Length];
                    for (int c = 0; c < fields.Length; c++)
                        columns[c] = await rgReader.ReadColumnAsync(fields[c]);

                    int rowCount = (int)rgReader.RowCount;
                    for (int r = 0; r < rowCount; r++)
                    {
                        string dateStr = GetDateString(columns[dateColIdx], r);
                        if (string.IsNullOrEmpty(dateStr)) continue;

                        var stockPrices = new Dictionary<string, double>();
                        foreach (var (idx, stockId) in stockColumns)
                        {
                            double price = GetDoubleValue(columns[idx], r);
                            if (price > 0)
                                stockPrices[stockId] = price;
                        }

                        if (stockPrices.Count > 0)
                            result[dateStr] = stockPrices;
                    }
                }

                Console.WriteLine($"[SCREENER] Loaded close prices parquet: {result.Count} dates, " +
                    $"{stockColumns.Count} stock columns");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to load close parquet: {ex.Message}");
            }

            return result;
        }

        private static Dictionary<string, Dictionary<string, double>> LoadCloseCsv(string path)
        {
            var result = new Dictionary<string, Dictionary<string, double>>();

            if (!File.Exists(path))
            {
                Console.WriteLine($"[ERROR] Close prices CSV not found: {path}");
                return result;
            }

            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length < 2) return result;

            string header = lines[0].TrimStart('\uFEFF');
            var headers = header.Split(',');
            // headers[0] = "date", headers[1..] = stock IDs

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var parts = lines[i].Split(',');
                string dateStr = parts[0].Trim();

                var stockPrices = new Dictionary<string, double>();
                for (int j = 1; j < parts.Length && j < headers.Length; j++)
                {
                    if (double.TryParse(parts[j], NumberStyles.Any, CultureInfo.InvariantCulture, out double price) && price > 0)
                        stockPrices[headers[j].Trim()] = price;
                }

                if (stockPrices.Count > 0)
                    result[dateStr] = stockPrices;
            }

            return result;
        }

        private static string GetDateString(DataColumn col, int row)
        {
            var data = col.Data;
            if (data is DateTimeOffset[] dto)
                return dto[row].ToString("yyyy-MM-dd");
            if (data is DateTime[] dt)
                return dt[row].ToString("yyyy-MM-dd");
            if (data is string[] s)
                return s[row]?.Trim() ?? "";
            if (data is byte[][] b)
                return Encoding.UTF8.GetString(b[row] ?? Array.Empty<byte>()).Trim();
            if (data is long[] longs)
            {
                long val = longs[row];
                DateTime d;
                if (val > 1e16) d = new DateTime(1970, 1, 1).AddTicks(val / 100);
                else if (val > 1e13) d = new DateTime(1970, 1, 1).AddTicks(val * 10);
                else d = new DateTime(1970, 1, 1).AddMilliseconds(val);
                return d.ToString("yyyy-MM-dd");
            }
            return data.GetValue(row)?.ToString()?.Trim() ?? "";
        }

        private static double GetDoubleValue(DataColumn col, int row)
        {
            var data = col.Data;
            if (data is double[] d) return d[row];
            if (data is float[] f) return f[row];
            if (data is long[] l) return l[row];
            if (data is int[] i) return i[row];
            if (data is decimal[] dec) return (double)dec[row];
            if (data is double?[] nd && nd[row].HasValue) return nd[row]!.Value;
            if (data is float?[] nf && nf[row].HasValue) return nf[row]!.Value;
            return 0;
        }

        #endregion
    }
}
