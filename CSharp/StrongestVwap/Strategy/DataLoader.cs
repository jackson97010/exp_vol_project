using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;
using StrongestVwap.Core;
using StrongestVwap.Core.Models;

namespace StrongestVwap.Strategy
{
    public class DataLoader
    {
        public static Dictionary<string, GroupDefinition> LoadGroupCsv(string path)
        {
            var groups = new Dictionary<string, GroupDefinition>();

            if (!File.Exists(path))
            {
                System.Console.WriteLine($"[WARNING] Group CSV not found: {path}");
                return groups;
            }

            var lines = File.ReadAllLines(path, Encoding.UTF8);
            bool isHeader = true;

            foreach (var line in lines)
            {
                if (isHeader) { isHeader = false; continue; }
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = line.Split(',');
                if (parts.Length < 2) continue;

                string groupName = parts[0].Trim();
                string stockId = parts[1].Trim();

                if (!groups.TryGetValue(groupName, out var gd))
                {
                    gd = new GroupDefinition { GroupName = groupName };
                    groups[groupName] = gd;
                }

                if (!gd.MemberStockIds.Contains(stockId))
                    gd.MemberStockIds.Add(stockId);
            }

            System.Console.WriteLine($"[DATA] Loaded {groups.Count} groups from {path}");
            return groups;
        }

        /// <summary>
        /// Load groups from screening_results.csv for a specific date.
        /// CSV format: date,stock_id,stock_name,category,avg_amount_20d,...
        /// Groups are built from the 'category' column.
        /// </summary>
        public static Dictionary<string, GroupDefinition> LoadGroupsFromScreeningCsv(
            string csvPath, string targetDate)
        {
            var groups = new Dictionary<string, GroupDefinition>();

            if (!File.Exists(csvPath))
            {
                System.Console.WriteLine($"[WARNING] Screening CSV not found: {csvPath}");
                return groups;
            }

            var lines = File.ReadAllLines(csvPath, Encoding.UTF8);
            if (lines.Length == 0) return groups;

            // Parse header — remove BOM if present
            string header = lines[0].TrimStart('\uFEFF');
            var headers = header.Split(',');

            int dateIdx = -1, stockIdIdx = -1, categoryIdx = -1;
            for (int i = 0; i < headers.Length; i++)
            {
                string h = headers[i].Trim().ToLowerInvariant();
                if (h == "date" || h == "日期") dateIdx = i;
                else if (h == "stock_id" || h == "code" || h == "代碼") stockIdIdx = i;
                else if (h == "category" || h == "stock_category" || h == "族群" || h == "group") categoryIdx = i;
            }

            if (dateIdx < 0 || stockIdIdx < 0 || categoryIdx < 0)
            {
                System.Console.WriteLine($"[WARNING] Screening CSV missing required columns (date/stock_id/category)");
                return groups;
            }

            int totalMembers = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var parts = lines[i].Split(',');
                if (parts.Length <= Math.Max(dateIdx, Math.Max(stockIdIdx, categoryIdx))) continue;

                string date = parts[dateIdx].Trim();
                if (date != targetDate) continue;

                string stockId = parts[stockIdIdx].Trim();
                string category = parts[categoryIdx].Trim();

                if (string.IsNullOrEmpty(stockId) || string.IsNullOrEmpty(category)) continue;

                if (!groups.TryGetValue(category, out var gd))
                {
                    gd = new GroupDefinition { GroupName = category };
                    groups[category] = gd;
                }

                if (!gd.MemberStockIds.Contains(stockId))
                {
                    gd.MemberStockIds.Add(stockId);
                    totalMembers++;
                }
            }

            System.Console.WriteLine($"[DATA] Loaded {groups.Count} groups, {totalMembers} stocks from screening CSV for {targetDate}");
            return groups;
        }

        /// <summary>
        /// Load stock IDs from screening CSV for a specific date (date + stock_id only, no category needed).
        /// </summary>
        public static List<string> LoadStockIdsFromScreeningCsv(string csvPath, string targetDate)
        {
            var stockIds = new List<string>();

            if (!File.Exists(csvPath))
            {
                System.Console.WriteLine($"[WARNING] Screening CSV not found: {csvPath}");
                return stockIds;
            }

            var lines = File.ReadAllLines(csvPath, Encoding.UTF8);
            if (lines.Length == 0) return stockIds;

            string header = lines[0].TrimStart('\uFEFF');
            var headers = header.Split(',');

            int dateIdx = -1, stockIdIdx = -1;
            for (int i = 0; i < headers.Length; i++)
            {
                string h = headers[i].Trim().ToLowerInvariant();
                if (h == "date" || h == "日期") dateIdx = i;
                else if (h == "stock_id" || h == "code" || h == "代碼") stockIdIdx = i;
            }

            if (dateIdx < 0 || stockIdIdx < 0)
            {
                System.Console.WriteLine($"[WARNING] Screening CSV missing required columns (date/stock_id)");
                return stockIds;
            }

            var seen = new HashSet<string>();
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var parts = lines[i].Split(',');
                if (parts.Length <= Math.Max(dateIdx, stockIdIdx)) continue;

                string date = parts[dateIdx].Trim();
                if (date != targetDate) continue;

                string sid = parts[stockIdIdx].Trim();
                if (!string.IsNullOrEmpty(sid) && seen.Add(sid))
                    stockIds.Add(sid);
            }

            System.Console.WriteLine($"[DATA] Loaded {stockIds.Count} stock IDs from screening CSV for {targetDate}");
            return stockIds;
        }

        public static List<RawTick> LoadTickDataParquet(string path)
        {
            return Task.Run(async () => await LoadTickDataParquetAsync(path)).GetAwaiter().GetResult();
        }

        private static async Task<List<RawTick>> LoadTickDataParquetAsync(string path)
        {
            var ticks = new List<RawTick>();

            if (!File.Exists(path))
            {
                System.Console.WriteLine($"[WARNING] Tick data not found: {path}");
                return ticks;
            }

            try
            {
                using var stream = File.OpenRead(path);
                using var reader = await ParquetReader.CreateAsync(stream);

                var fields = reader.Schema.GetDataFields();
                var fieldNames = fields.Select(f => f.Name).ToList();

                int timeIdx = FindColumn(fieldNames, "time", "timestamp");
                int stockIdIdx = FindColumn(fieldNames, "stock_id", "code", "symbol");
                int priceIdx = FindColumn(fieldNames, "price", "close");
                int volumeIdx = FindColumn(fieldNames, "volume", "vol");
                int tradeCodeIdx = FindColumn(fieldNames, "trade_code", "tradeCode");
                int tickTypeIdx = FindColumn(fieldNames, "tick_type", "tickType");
                int askPrice0Idx = FindColumn(fieldNames, "ask_price_0", "askPrice0");
                int secTypeIdx = FindColumn(fieldNames, "security_type", "securityType");
                int prevCloseIdx = FindColumn(fieldNames, "previous_close", "prevClose", "prev_close");
                int monthAvgIdx = FindColumn(fieldNames, "monthly_avg_trading_value", "monthAvgVal");
                int todayCumIdx = FindColumn(fieldNames, "today_cumulative_value", "todayCumVal");
                int limitUpLockedIdx = FindColumn(fieldNames, "is_limit_up_locked", "limitUpLocked");
                int prevDayLimitUpIdx = FindColumn(fieldNames, "prev_day_limit_up", "prevDayLimitUp");

                for (int rg = 0; rg < reader.RowGroupCount; rg++)
                {
                    using var rgReader = reader.OpenRowGroupReader(rg);
                    var columns = new DataColumn[fields.Length];
                    for (int c = 0; c < fields.Length; c++)
                        columns[c] = await rgReader.ReadColumnAsync(fields[c]);

                    int rowCount = (int)rgReader.RowCount;
                    for (int r = 0; r < rowCount; r++)
                    {
                        var tick = new RawTick();

                        if (timeIdx >= 0) tick.Time = GetDateTime(columns[timeIdx], r);
                        if (stockIdIdx >= 0) tick.StockId = GetString(columns[stockIdIdx], r);
                        if (priceIdx >= 0) tick.Price = GetDouble(columns[priceIdx], r);
                        if (volumeIdx >= 0) tick.Volume = GetDouble(columns[volumeIdx], r);
                        if (tradeCodeIdx >= 0) tick.TradeCode = GetInt(columns[tradeCodeIdx], r);
                        if (tickTypeIdx >= 0) tick.TickType = GetString(columns[tickTypeIdx], r);
                        if (askPrice0Idx >= 0) tick.AskPrice0 = GetDouble(columns[askPrice0Idx], r);
                        if (secTypeIdx >= 0) tick.SecurityType = GetString(columns[secTypeIdx], r);
                        if (prevCloseIdx >= 0) tick.PreviousClose = GetDouble(columns[prevCloseIdx], r);
                        if (monthAvgIdx >= 0) tick.MonthlyAvgTradingValue = GetDouble(columns[monthAvgIdx], r);
                        if (todayCumIdx >= 0) tick.TodayCumulativeValue = GetDouble(columns[todayCumIdx], r);
                        if (limitUpLockedIdx >= 0) tick.IsLimitUpLocked = GetBool(columns[limitUpLockedIdx], r);
                        if (prevDayLimitUpIdx >= 0) tick.PrevDayLimitUp = GetBool(columns[prevDayLimitUpIdx], r);

                        ticks.Add(tick);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[ERROR] Failed to load tick data from {path}: {ex.Message}");
            }

            ticks.Sort((a, b) => a.Time.CompareTo(b.Time));
            return ticks;
        }

        public static Dictionary<string, StockStaticData> LoadStaticData(string path)
        {
            if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                return LoadStaticDataCsv(path);
            return Task.Run(async () => await LoadStaticDataAsync(path)).GetAwaiter().GetResult();
        }

        private static Dictionary<string, StockStaticData> LoadStaticDataCsv(string path)
        {
            var result = new Dictionary<string, StockStaticData>();
            if (!File.Exists(path))
            {
                System.Console.WriteLine($"[WARNING] Static CSV not found: {path}");
                return result;
            }

            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0) return result;

            string header = lines[0].TrimStart('\uFEFF');
            var headers = header.Split(',');
            var colMap = new Dictionary<string, int>();
            for (int i = 0; i < headers.Length; i++)
                colMap[headers[i].Trim().ToLowerInvariant()] = i;

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var parts = lines[i].Split(',');

                string sid = GetCsvField(parts, colMap, "stock_id");
                if (string.IsNullOrEmpty(sid)) continue;

                var sd = new StockStaticData { StockId = sid };
                sd.PreviousClose = GetCsvDouble(parts, colMap, "previous_close");
                sd.MonthlyAvgTradingValue = GetCsvDouble(parts, colMap, "monthly_avg_trading_value");
                sd.SecurityType = GetCsvField(parts, colMap, "security_type");
                sd.PrevClose0050 = GetCsvDouble(parts, colMap, "prev_close_0050");

                string plu = GetCsvField(parts, colMap, "prev_day_limit_up").ToLowerInvariant();
                sd.PrevDayLimitUp = plu == "true" || plu == "1";

                result[sid] = sd;
            }

            return result;
        }

        private static string GetCsvField(string[] parts, Dictionary<string, int> colMap, string key)
        {
            return colMap.TryGetValue(key, out int idx) && idx < parts.Length
                ? parts[idx].Trim() : "";
        }

        private static double GetCsvDouble(string[] parts, Dictionary<string, int> colMap, string key)
        {
            string val = GetCsvField(parts, colMap, key);
            return double.TryParse(val, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;
        }

        private static async Task<Dictionary<string, StockStaticData>> LoadStaticDataAsync(string path)
        {
            var result = new Dictionary<string, StockStaticData>();

            if (!File.Exists(path))
            {
                System.Console.WriteLine($"[WARNING] Static data not found: {path}");
                return result;
            }

            try
            {
                using var stream = File.OpenRead(path);
                using var reader = await ParquetReader.CreateAsync(stream);
                var fields = reader.Schema.GetDataFields();
                var fieldNames = fields.Select(f => f.Name).ToList();

                int stockIdIdx = FindColumn(fieldNames, "stock_id", "code", "symbol");
                int prevCloseIdx = FindColumn(fieldNames, "previous_close", "prevClose", "prev_close");
                int monthAvgIdx = FindColumn(fieldNames, "monthly_avg_trading_value", "monthAvgVal", "month_avg_val");
                int secTypeIdx = FindColumn(fieldNames, "security_type", "securityType");
                int prevDayLimitUpIdx = FindColumn(fieldNames, "prev_day_limit_up", "prevDayLimitUp");
                int prevClose0050Idx = FindColumn(fieldNames, "prev_close_0050");

                for (int rg = 0; rg < reader.RowGroupCount; rg++)
                {
                    using var rgReader = reader.OpenRowGroupReader(rg);
                    var columns = new DataColumn[fields.Length];
                    for (int c = 0; c < fields.Length; c++)
                        columns[c] = await rgReader.ReadColumnAsync(fields[c]);

                    int rowCount = (int)rgReader.RowCount;
                    for (int r = 0; r < rowCount; r++)
                    {
                        string sid = stockIdIdx >= 0 ? GetString(columns[stockIdIdx], r) : "";
                        if (string.IsNullOrEmpty(sid)) continue;

                        var sd = new StockStaticData { StockId = sid };
                        if (prevCloseIdx >= 0) sd.PreviousClose = GetDouble(columns[prevCloseIdx], r);
                        if (monthAvgIdx >= 0) sd.MonthlyAvgTradingValue = GetDouble(columns[monthAvgIdx], r);
                        if (secTypeIdx >= 0) sd.SecurityType = GetString(columns[secTypeIdx], r);
                        if (prevDayLimitUpIdx >= 0) sd.PrevDayLimitUp = GetBool(columns[prevDayLimitUpIdx], r);
                        if (prevClose0050Idx >= 0) sd.PrevClose0050 = GetDouble(columns[prevClose0050Idx], r);

                        result[sid] = sd;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[ERROR] Failed to load static data: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Load tick data from per-stock parquet files in a directory.
        /// Each file: {stockId}.parquet with columns: type, time, price, volume, vwap, day_high
        /// Only loads Trade ticks (type == "Trade").
        /// </summary>
        public static List<RawTick> LoadPerStockTickData(string baseDir, string date,
            IEnumerable<string> stockIds, Dictionary<string, StockStaticData> staticData,
            bool loadDepth = false)
        {
            string dateDir = Path.Combine(baseDir, date);
            if (!Directory.Exists(dateDir))
            {
                System.Console.WriteLine($"[WARNING] Tick data directory not found: {dateDir}");
                return new List<RawTick>();
            }

            var allTicks = new List<RawTick>();
            int loaded = 0, skipped = 0;

            foreach (var sid in stockIds)
            {
                string filePath = Path.Combine(dateDir, $"{sid}.parquet");
                if (!File.Exists(filePath))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var stockTicks = LoadSingleStockParquet(filePath, sid, staticData, loadDepth);
                    allTicks.AddRange(stockTicks);
                    loaded++;
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[WARNING] Failed to load {sid}: {ex.Message}");
                    skipped++;
                }
            }

            System.Console.WriteLine($"[DATA] Loaded per-stock tick data: {loaded} stocks, {skipped} skipped, {allTicks.Count} ticks total");
            allTicks.Sort((a, b) => a.Time.CompareTo(b.Time));
            return allTicks;
        }

        private static List<RawTick> LoadSingleStockParquet(string filePath, string stockId,
            Dictionary<string, StockStaticData> staticData, bool loadDepth = false)
        {
            return Task.Run(async () =>
            {
                var ticks = new List<RawTick>();
                using var stream = File.OpenRead(filePath);
                using var reader = await ParquetReader.CreateAsync(stream);

                var fields = reader.Schema.GetDataFields();
                var fieldNames = fields.Select(f => f.Name).ToList();

                int typeIdx = FindColumn(fieldNames, "type");
                int timeIdx = FindColumn(fieldNames, "time", "timestamp");
                int priceIdx = FindColumn(fieldNames, "price");
                int volumeIdx = FindColumn(fieldNames, "volume", "vol");
                int vwapIdx = FindColumn(fieldNames, "vwap");
                int dayHighIdx = FindColumn(fieldNames, "day_high");
                int low1mIdx = FindColumn(fieldNames, "low_1m");
                int low3mIdx = FindColumn(fieldNames, "low_3m");
                int low5mIdx = FindColumn(fieldNames, "low_5m");
                int low7mIdx = FindColumn(fieldNames, "low_7m");
                int low10mIdx = FindColumn(fieldNames, "low_10m");
                int low15mIdx = FindColumn(fieldNames, "low_15m");
                int bidVol5Idx = FindColumn(fieldNames, "bid_volume_5level");
                int askVol5Idx = FindColumn(fieldNames, "ask_volume_5level");
                int tickTypeIntIdx = FindColumn(fieldNames, "tick_type");
                int pct5minIdx = FindColumn(fieldNames, "pct_5min");
                int ratio15s300sIdx = FindColumn(fieldNames, "ratio_15s_300s");
                int bidAskRatioIdx = FindColumn(fieldNames, "bid_ask_ratio");
                int vwap5mIdx = FindColumn(fieldNames, "vwap_5m");

                // Get static data for this stock
                double prevClose = 0, monthAvg = 0;
                string secType = "";
                bool prevDayLimitUp = false;
                if (staticData.TryGetValue(stockId, out var sd))
                {
                    prevClose = sd.PreviousClose;
                    monthAvg = sd.MonthlyAvgTradingValue;
                    secType = sd.SecurityType;
                    prevDayLimitUp = sd.PrevDayLimitUp;
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
                        string rowType = typeIdx >= 0 ? GetString(columns[typeIdx], r) : "Trade";

                        // Skip Depth rows if not needed
                        if (rowType == "Depth" && !loadDepth) continue;
                        if (rowType != "Trade" && rowType != "Depth") continue;

                        var tick = new RawTick
                        {
                            StockId = stockId,
                            RowType = rowType,
                            TradeCode = rowType == "Trade" ? 1 : 0,
                            PreviousClose = prevClose,
                            MonthlyAvgTradingValue = monthAvg,
                            SecurityType = secType,
                            PrevDayLimitUp = prevDayLimitUp,
                        };

                        if (timeIdx >= 0) tick.Time = GetDateTime(columns[timeIdx], r);

                        if (rowType == "Trade")
                        {
                            if (priceIdx >= 0) tick.Price = GetDouble(columns[priceIdx], r);
                            if (volumeIdx >= 0) tick.Volume = GetDouble(columns[volumeIdx], r);
                            if (low1mIdx >= 0) tick.Low1m = GetDouble(columns[low1mIdx], r);
                            if (low3mIdx >= 0) tick.Low3m = GetDouble(columns[low3mIdx], r);
                            if (low5mIdx >= 0) tick.Low5m = GetDouble(columns[low5mIdx], r);
                            if (low7mIdx >= 0) tick.Low7m = GetDouble(columns[low7mIdx], r);
                            if (low10mIdx >= 0) tick.Low10m = GetDouble(columns[low10mIdx], r);
                            if (low15mIdx >= 0) tick.Low15m = GetDouble(columns[low15mIdx], r);
                            if (tickTypeIntIdx >= 0)
                            {
                                string ttStr = GetString(columns[tickTypeIntIdx], r);
                                int.TryParse(ttStr, out int ttVal);
                                tick.TickTypeInt = ttVal;
                            }
                            if (pct5minIdx >= 0) tick.Pct5min = GetDouble(columns[pct5minIdx], r);
                            if (ratio15s300sIdx >= 0) tick.Ratio15s300s = GetDouble(columns[ratio15s300sIdx], r);
                            if (bidAskRatioIdx >= 0) tick.BidAskRatio = GetDouble(columns[bidAskRatioIdx], r);
                            if (vwap5mIdx >= 0) tick.Vwap5m = GetDouble(columns[vwap5mIdx], r);

                            // Skip Trade ticks with zero price
                            if (tick.Price <= 0) continue;
                        }
                        else // Depth
                        {
                            if (bidVol5Idx >= 0) tick.BidVolume5Level = GetDouble(columns[bidVol5Idx], r);
                            if (askVol5Idx >= 0) tick.AskVolume5Level = GetDouble(columns[askVol5Idx], r);
                        }

                        ticks.Add(tick);
                    }
                }

                return ticks;
            }).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Load daily liquidity threshold from wide-format parquet.
        /// Returns: Dictionary[dateString, Dictionary[stockId, threshold]]
        /// </summary>
        public static Dictionary<string, Dictionary<string, double>> LoadLiquidityThreshold(string path)
        {
            return Task.Run(async () => await LoadLiquidityThresholdAsync(path)).GetAwaiter().GetResult();
        }

        private static async Task<Dictionary<string, Dictionary<string, double>>> LoadLiquidityThresholdAsync(string path)
        {
            var result = new Dictionary<string, Dictionary<string, double>>();

            if (!File.Exists(path))
            {
                System.Console.WriteLine($"[WARNING] Liquidity threshold file not found: {path}");
                return result;
            }

            try
            {
                using var stream = File.OpenRead(path);
                using var reader = await ParquetReader.CreateAsync(stream);
                var fields = reader.Schema.GetDataFields();
                var fieldNames = fields.Select(f => f.Name).ToList();

                // Find date column (last column named "date" or "__index_level_0__")
                int dateColIdx = -1;
                for (int i = fieldNames.Count - 1; i >= 0; i--)
                {
                    if (fieldNames[i] == "date" || fieldNames[i] == "__index_level_0__")
                    {
                        dateColIdx = i;
                        break;
                    }
                }

                if (dateColIdx < 0)
                {
                    System.Console.WriteLine("[WARNING] No date column found in liquidity threshold parquet");
                    return result;
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
                        DateTime dt = GetDateTime(columns[dateColIdx], r);
                        string dateStr = dt.ToString("yyyy-MM-dd");

                        var stockThresholds = new Dictionary<string, double>();
                        for (int c = 0; c < fields.Length; c++)
                        {
                            if (c == dateColIdx) continue;
                            double val = GetDouble(columns[c], r);
                            if (double.IsNaN(val) || val <= 0) continue;
                            stockThresholds[fieldNames[c]] = val;
                        }

                        if (stockThresholds.Count > 0)
                            result[dateStr] = stockThresholds;
                    }
                }

                System.Console.WriteLine($"[DATA] Loaded liquidity thresholds for {result.Count} dates");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[ERROR] Failed to load liquidity threshold: {ex.Message}");
            }

            return result;
        }

        #region Column helpers

        private static int FindColumn(List<string> fieldNames, params string[] candidates)
        {
            foreach (var c in candidates)
            {
                int idx = fieldNames.IndexOf(c);
                if (idx >= 0) return idx;
            }
            return -1;
        }

        private static DateTime GetDateTime(DataColumn col, int row)
        {
            var data = col.Data;
            if (data is DateTimeOffset[] dto) return dto[row].DateTime;
            if (data is DateTimeOffset?[] ndto && ndto[row].HasValue) return ndto[row]!.Value.DateTime;
            if (data is DateTime[] dt) return dt[row];
            if (data is DateTime?[] ndt && ndt[row].HasValue) return ndt[row]!.Value;

            long val = 0;
            if (data is long[] longs) val = longs[row];
            else if (data is long?[] nlongs && nlongs[row].HasValue) val = nlongs[row]!.Value;
            else return DateTime.MinValue;

            // Timestamps in parquet are stored as UTC epoch but represent local time
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            if (val > 1e16) return epoch.AddTicks(val / 100);  // ns
            if (val > 1e13) return epoch.AddTicks(val * 10);   // us
            return epoch.AddMilliseconds(val);
        }

        private static double GetDouble(DataColumn col, int row)
        {
            var data = col.Data;
            if (data is double[] d) return d[row];
            if (data is float[] f) return f[row];
            if (data is long[] l) return l[row];
            if (data is int[] i) return i[row];
            if (data is decimal[] dec) return (double)dec[row];
            if (data is double?[] nd && nd[row].HasValue) return nd[row]!.Value;
            if (data is float?[] nf && nf[row].HasValue) return nf[row]!.Value;
            if (data is long?[] nl && nl[row].HasValue) return nl[row]!.Value;
            if (data is int?[] ni && ni[row].HasValue) return ni[row]!.Value;
            return 0;
        }

        private static int GetInt(DataColumn col, int row)
        {
            return (int)GetDouble(col, row);
        }

        private static string GetString(DataColumn col, int row)
        {
            var data = col.Data;
            if (data is string[] s) return s[row] ?? "";
            if (data is byte[][] b) return Encoding.UTF8.GetString(b[row] ?? Array.Empty<byte>());
            return data.GetValue(row)?.ToString() ?? "";
        }

        private static bool GetBool(DataColumn col, int row)
        {
            var data = col.Data;
            if (data is bool[] b) return b[row];
            if (data is bool?[] nb) return nb[row] ?? false;
            if (data is int[] i) return i[row] != 0;
            if (data is long[] l) return l[row] != 0;
            return false;
        }

        #endregion
    }

    public class StockStaticData
    {
        public string StockId { get; set; } = "";
        public double PreviousClose { get; set; }
        public double MonthlyAvgTradingValue { get; set; }
        public string SecurityType { get; set; } = "";
        public bool PrevDayLimitUp { get; set; }
        public double PrevClose0050 { get; set; }
    }
}
