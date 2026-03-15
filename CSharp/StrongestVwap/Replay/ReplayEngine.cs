using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;

namespace StrongestVwap.Replay
{
    #region Data Models

    public class StockLiveState
    {
        public string StockId { get; set; } = "";
        public string StockName { get; set; } = "";
        public string Category { get; set; } = "";
        public double PrevClose { get; set; }
        public double LastPrice { get; set; }
        public double Vwap { get; set; }
        public double DayHigh { get; set; }
        public double CumulativeVolume { get; set; }
        public double CumulativeValue { get; set; }
        public double AvgAmount20d { get; set; }
        public double TodayAmountFromCsv { get; set; }
        public double PriceChangePct => PrevClose > 0 ? (LastPrice - PrevClose) / PrevClose : 0;
    }

    public class GroupSnapshot
    {
        public string GroupName { get; set; } = "";
        public int Rank { get; set; }
        public double AvgPriceChangePct { get; set; }
        public double ValRatio { get; set; }
        public double TotalMonthlyVal { get; set; }
        public int ValidMemberCount { get; set; }
        public bool IsValid { get; set; }
        public List<MemberSnapshot> Members { get; set; } = new();
    }

    public class MemberSnapshot
    {
        public string StockId { get; set; } = "";
        public string StockName { get; set; } = "";
        public double PriceChangePct { get; set; }
        public double LastPrice { get; set; }
        public double PrevClose { get; set; }
        public double Vwap { get; set; }
        public double DayHigh { get; set; }
        public double CumulativeValue { get; set; }
        public double AvgAmount20d { get; set; }
        public int RankInGroup { get; set; }
        public bool IsSelected { get; set; }
    }

    public class TimeSnapshot
    {
        public string Time { get; set; } = "";  // "HH:mm:ss"
        public int TotalSeconds { get; set; }    // seconds since 09:00:00
        public List<GroupSnapshot> Groups { get; set; } = new();
        public int ValidGroupCount { get; set; }
        public int TotalSelectedStocks { get; set; }
    }

    public class ReplayResult
    {
        public string Date { get; set; } = "";
        public Dictionary<string, object> Config { get; set; } = new();
        public List<TimeSnapshot> Snapshots { get; set; } = new();
    }

    #endregion

    #region Internal Tick Event

    internal struct TickEvent : IComparable<TickEvent>
    {
        public DateTime Time;
        public string StockId;
        public double Price;
        public double Volume;
        public double Vwap;
        public double DayHigh;

        public int CompareTo(TickEvent other) => Time.CompareTo(other.Time);
    }

    #endregion

    /// <summary>
    /// Replay engine: loads screening CSV + close.parquet + per-stock tick data,
    /// merges ticks chronologically, and creates second-by-second group/member snapshots.
    /// </summary>
    public class ReplayEngine
    {
        // Config defaults (strong group screening thresholds)
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

        // State
        private Dictionary<string, StockLiveState> _stockStates = new();
        private Dictionary<string, List<string>> _groupMembers = new(); // category -> list of stockIds
        private Dictionary<string, double> _groupValRatios = new();     // category -> val_ratio from CSV

        /// <summary>
        /// Construct with default config values.
        /// </summary>
        public ReplayEngine()
        {
            _memberMinMonthVal = 200_000_000;
            _groupMinMonthVal = 3_000_000_000;
            _groupMinAvgPctChg = 0.01;
            _groupMinValRatio = 1.2;
            _isWeightedAvg = false;
            _groupValidTopN = 20;
            _topGroupRankThreshold = 10;
            _topGroupMaxSelect = 1;
            _normalGroupMaxSelect = 1;
            _entryMinVwapPctChg = 0.04;
        }

        /// <summary>
        /// Construct with a StrategyConfig dictionary for overriding defaults.
        /// Keys match the strong_group section of the YAML config.
        /// </summary>
        public ReplayEngine(Dictionary<string, object> config)
        {
            _memberMinMonthVal = GetDouble(config, "member_min_month_trading_val", 200_000_000);
            _groupMinMonthVal = GetDouble(config, "group_min_month_trading_val", 3_000_000_000);
            _groupMinAvgPctChg = GetDouble(config, "group_min_avg_pct_chg", 0.01);
            _groupMinValRatio = GetDouble(config, "group_min_val_ratio", 1.2);
            _isWeightedAvg = GetBool(config, "is_weighted_avg", false);
            _groupValidTopN = GetInt(config, "group_valid_top_n", 20);
            _topGroupRankThreshold = GetInt(config, "top_group_rank_threshold", 10);
            _topGroupMaxSelect = GetInt(config, "top_group_max_select", 1);
            _normalGroupMaxSelect = GetInt(config, "normal_group_max_select", 1);
            _entryMinVwapPctChg = GetDouble(config, "entry_min_vwap_pct_chg", 0.04);
        }

        /// <summary>
        /// Main entry point: run the replay for a single date.
        /// </summary>
        /// <param name="date">Target date in "yyyy-MM-dd" format.</param>
        /// <param name="screeningCsvPath">Path to screening_results.csv.</param>
        /// <param name="closeParquetPath">Path to close.parquet (wide-format).</param>
        /// <param name="tickDataBasePath">Base path for tick data, e.g. "D:\feature_data\feature".</param>
        /// <returns>ReplayResult containing all second-by-second snapshots.</returns>
        public ReplayResult Run(string date, string screeningCsvPath, string closeParquetPath, string tickDataBasePath)
        {
            Console.WriteLine($"\n{'=',-60}");
            Console.WriteLine($"[REPLAY] Starting replay for date: {date}");
            Console.WriteLine($"{'=',-60}");

            // 1. Load screening CSV for target date
            var screeningRecords = LoadScreeningCsv(screeningCsvPath, date);
            if (screeningRecords.Count == 0)
            {
                Console.WriteLine("[REPLAY] No screening records for target date. Aborting.");
                return new ReplayResult { Date = date, Config = BuildConfigDict() };
            }

            // Build group membership and stock states from screening data
            BuildGroupsAndStates(screeningRecords);
            Console.WriteLine($"[REPLAY] Groups: {_groupMembers.Count}, Unique stocks: {_stockStates.Count}");

            // 2. Load prev close from close.parquet
            int prevCloseLoaded = LoadPrevCloseFromParquet(closeParquetPath, date);
            Console.WriteLine($"[REPLAY] Loaded prev_close for {prevCloseLoaded} stocks");

            // 3. Load tick data for all screening stocks
            var mergedTicks = LoadAndMergeTickData(tickDataBasePath, date);
            Console.WriteLine($"[REPLAY] Total merged ticks: {mergedTicks.Count}");

            if (mergedTicks.Count == 0)
            {
                Console.WriteLine("[REPLAY] No tick data loaded. Aborting.");
                return new ReplayResult { Date = date, Config = BuildConfigDict() };
            }

            // 4. Process ticks and create snapshots
            var snapshots = ProcessTicks(mergedTicks);
            Console.WriteLine($"[REPLAY] Generated {snapshots.Count} time snapshots");

            // 5. Build result
            var result = new ReplayResult
            {
                Date = date,
                Config = BuildConfigDict(),
                Snapshots = snapshots
            };

            return result;
        }

        #region Step 1: Screening CSV

        private List<ScreeningEntry> LoadScreeningCsv(string path, string targetDate)
        {
            var records = new List<ScreeningEntry>();

            if (!File.Exists(path))
            {
                Console.WriteLine($"[REPLAY] Screening CSV not found: {path}");
                return records;
            }

            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length < 2) return records;

            // Parse header (handle BOM)
            string header = lines[0].TrimStart('\uFEFF');
            var headers = header.Split(',').Select(h => h.Trim()).ToList();

            int dateIdx = FindHeaderIndex(headers, "date", "\u65E5\u671F");
            int stockIdIdx = FindHeaderIndex(headers, "stock_id", "\u4EE3\u78BC", "code");
            int stockNameIdx = FindHeaderIndex(headers, "stock_name", "\u80A1\u7968\u540D\u7A31", "name");
            int categoryIdx = FindHeaderIndex(headers, "category", "stock_category", "\u65CF\u7FA4", "group");
            int avgAmount20dIdx = FindHeaderIndex(headers, "avg_amount_20d");
            int todayAmountIdx = FindHeaderIndex(headers, "today_amount");
            int groupMa20SumIdx = FindHeaderIndex(headers, "group_ma20_sum");
            int groupTodaySumIdx = FindHeaderIndex(headers, "group_today_sum");
            int valRatioIdx = FindHeaderIndex(headers, "val_ratio");

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var parts = lines[i].Split(',');

                string rowDate = dateIdx >= 0 && dateIdx < parts.Length ? parts[dateIdx].Trim() : "";
                if (rowDate != targetDate) continue;

                var rec = new ScreeningEntry { Date = rowDate };
                if (stockIdIdx >= 0 && stockIdIdx < parts.Length) rec.StockId = parts[stockIdIdx].Trim();
                if (stockNameIdx >= 0 && stockNameIdx < parts.Length) rec.StockName = parts[stockNameIdx].Trim();
                if (categoryIdx >= 0 && categoryIdx < parts.Length) rec.Category = parts[categoryIdx].Trim();
                if (avgAmount20dIdx >= 0 && avgAmount20dIdx < parts.Length)
                    double.TryParse(parts[avgAmount20dIdx], NumberStyles.Any, CultureInfo.InvariantCulture, out rec.AvgAmount20d);
                if (todayAmountIdx >= 0 && todayAmountIdx < parts.Length)
                    double.TryParse(parts[todayAmountIdx], NumberStyles.Any, CultureInfo.InvariantCulture, out rec.TodayAmount);
                if (groupMa20SumIdx >= 0 && groupMa20SumIdx < parts.Length)
                    double.TryParse(parts[groupMa20SumIdx], NumberStyles.Any, CultureInfo.InvariantCulture, out rec.GroupMa20Sum);
                if (groupTodaySumIdx >= 0 && groupTodaySumIdx < parts.Length)
                    double.TryParse(parts[groupTodaySumIdx], NumberStyles.Any, CultureInfo.InvariantCulture, out rec.GroupTodaySum);
                if (valRatioIdx >= 0 && valRatioIdx < parts.Length)
                    double.TryParse(parts[valRatioIdx], NumberStyles.Any, CultureInfo.InvariantCulture, out rec.ValRatio);

                records.Add(rec);
            }

            Console.WriteLine($"[REPLAY] Loaded {records.Count} screening records for date {targetDate}");
            return records;
        }

        private void BuildGroupsAndStates(List<ScreeningEntry> records)
        {
            _stockStates.Clear();
            _groupMembers.Clear();
            _groupValRatios.Clear();

            foreach (var rec in records)
            {
                // Build group membership (a stock can appear in multiple categories)
                if (!_groupMembers.TryGetValue(rec.Category, out var memberList))
                {
                    memberList = new List<string>();
                    _groupMembers[rec.Category] = memberList;
                }
                if (!memberList.Contains(rec.StockId))
                    memberList.Add(rec.StockId);

                // Store val_ratio per group (use last seen value from CSV)
                _groupValRatios[rec.Category] = rec.ValRatio;

                // Build stock state (if same stock appears in multiple categories, use first seen values)
                if (!_stockStates.ContainsKey(rec.StockId))
                {
                    _stockStates[rec.StockId] = new StockLiveState
                    {
                        StockId = rec.StockId,
                        StockName = rec.StockName,
                        Category = rec.Category,
                        AvgAmount20d = rec.AvgAmount20d,
                        TodayAmountFromCsv = rec.TodayAmount
                    };
                }
            }
        }

        #endregion

        #region Step 2: Close Parquet (prev_close)

        private int LoadPrevCloseFromParquet(string closeParquetPath, string targetDate)
        {
            if (!File.Exists(closeParquetPath))
            {
                Console.WriteLine($"[REPLAY] Close parquet not found: {closeParquetPath}");
                return 0;
            }

            // Parse target date
            if (!DateTime.TryParseExact(targetDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var targetDt))
            {
                Console.WriteLine($"[REPLAY] Invalid date format: {targetDate}");
                return 0;
            }

            var prevCloses = Task.Run(async () =>
                await LoadPrevCloseAsync(closeParquetPath, targetDt)).GetAwaiter().GetResult();

            int count = 0;
            foreach (var kvp in prevCloses)
            {
                if (_stockStates.TryGetValue(kvp.Key, out var state))
                {
                    state.PrevClose = kvp.Value;
                    count++;
                }
            }

            return count;
        }

        private static async Task<Dictionary<string, double>> LoadPrevCloseAsync(string path, DateTime targetDate)
        {
            var result = new Dictionary<string, double>();

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

                if (dateColIdx < 0)
                {
                    Console.WriteLine("[REPLAY] Date column not found in close.parquet");
                    return result;
                }

                // Stock columns = all columns except date
                var stockColumns = new List<(int idx, string stockId)>();
                for (int i = 0; i < fieldNames.Count; i++)
                {
                    if (i == dateColIdx) continue;
                    stockColumns.Add((i, fieldNames[i]));
                }

                // Read all rows, find the row immediately before targetDate
                var allRowData = new Dictionary<string, Dictionary<string, double>>(); // "yyyy-MM-dd" -> stockId -> price

                for (int rg = 0; rg < reader.RowGroupCount; rg++)
                {
                    using var rgReader = reader.OpenRowGroupReader(rg);
                    var columns = new DataColumn[fields.Length];
                    for (int c = 0; c < fields.Length; c++)
                        columns[c] = await rgReader.ReadColumnAsync(fields[c]);

                    int rowCount = (int)rgReader.RowCount;
                    for (int r = 0; r < rowCount; r++)
                    {
                        DateTime rowDate = GetDateTimeFromColumn(columns[dateColIdx], r);
                        if (rowDate == DateTime.MinValue) continue;

                        string dateStr = rowDate.Date.ToString("yyyy-MM-dd");

                        // Only collect dates that are before the target date
                        if (rowDate.Date < targetDate.Date)
                        {
                            if (!allRowData.ContainsKey(dateStr))
                            {
                                var stockPrices = new Dictionary<string, double>();
                                foreach (var (idx, stockId) in stockColumns)
                                {
                                    double price = GetDoubleFromColumn(columns[idx], r);
                                    if (!double.IsNaN(price) && price > 0)
                                        stockPrices[stockId] = price;
                                }
                                allRowData[dateStr] = stockPrices;
                            }
                        }
                    }
                }

                // Find the most recent date before targetDate
                if (allRowData.Count > 0)
                {
                    string prevDateStr = allRowData.Keys
                        .OrderByDescending(d => d)
                        .First();

                    result = allRowData[prevDateStr];
                    Console.WriteLine($"[REPLAY] Using prev_close from date: {prevDateStr} ({result.Count} stocks)");
                }
                else
                {
                    Console.WriteLine("[REPLAY] No previous trading date found in close.parquet");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[REPLAY] Error reading close.parquet: {ex.Message}");
            }

            return result;
        }

        #endregion

        #region Step 3: Per-Stock Tick Data

        private List<TickEvent> LoadAndMergeTickData(string tickDataBasePath, string date)
        {
            var allTicks = new List<TickEvent>();
            var stockIds = _stockStates.Keys.ToList();
            int loadedCount = 0;
            int failedCount = 0;

            Console.WriteLine($"[REPLAY] Loading tick data for {stockIds.Count} stocks from {tickDataBasePath}/{date}/");

            foreach (var stockId in stockIds)
            {
                string tickPath = Path.Combine(tickDataBasePath, date, $"{stockId}.parquet");
                if (!File.Exists(tickPath))
                {
                    failedCount++;
                    continue;
                }

                try
                {
                    var ticks = LoadStockTickData(tickPath, stockId);
                    if (ticks.Count > 0)
                    {
                        allTicks.AddRange(ticks);
                        loadedCount++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[REPLAY] Error loading ticks for {stockId}: {ex.Message}");
                    failedCount++;
                }
            }

            Console.WriteLine($"[REPLAY] Loaded tick data: {loadedCount} stocks OK, {failedCount} skipped, {allTicks.Count} total ticks");

            // Sort all ticks by time
            allTicks.Sort();

            return allTicks;
        }

        private List<TickEvent> LoadStockTickData(string path, string stockId)
        {
            return Task.Run(async () => await LoadStockTickDataAsync(path, stockId)).GetAwaiter().GetResult();
        }

        private static async Task<List<TickEvent>> LoadStockTickDataAsync(string path, string stockId)
        {
            var ticks = new List<TickEvent>();

            using var stream = File.OpenRead(path);
            using var reader = await ParquetReader.CreateAsync(stream);
            var fields = reader.Schema.GetDataFields();
            var fieldNames = fields.Select(f => f.Name).ToList();

            // Required columns
            int timeIdx = FindColumn(fieldNames, "time", "datetime", "timestamp");
            int priceIdx = FindColumn(fieldNames, "price", "close");
            int volumeIdx = FindColumn(fieldNames, "volume", "vol");
            int vwapIdx = FindColumn(fieldNames, "vwap");
            int dayHighIdx = FindColumn(fieldNames, "day_high", "high");
            int typeIdx = FindColumn(fieldNames, "type", "trade_type", "tick_type");

            if (timeIdx < 0 || priceIdx < 0)
            {
                return ticks;
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
                    // Filter: only Trade ticks
                    if (typeIdx >= 0)
                    {
                        string typeStr = GetStringFromColumn(columns[typeIdx], r);
                        if (!string.IsNullOrEmpty(typeStr) &&
                            !typeStr.Equals("Trade", StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    DateTime time = GetDateTimeFromColumn(columns[timeIdx], r);
                    if (time == DateTime.MinValue) continue;

                    // Only process trading hours (09:00:00 - 13:30:00)
                    var tod = time.TimeOfDay;
                    if (tod < new TimeSpan(9, 0, 0) || tod > new TimeSpan(13, 30, 0))
                        continue;

                    double price = GetDoubleFromColumn(columns[priceIdx], r);
                    if (price <= 0) continue;

                    double volume = volumeIdx >= 0 ? GetDoubleFromColumn(columns[volumeIdx], r) : 0;
                    double vwap = vwapIdx >= 0 ? GetDoubleFromColumn(columns[vwapIdx], r) : 0;
                    double dayHigh = dayHighIdx >= 0 ? GetDoubleFromColumn(columns[dayHighIdx], r) : 0;

                    ticks.Add(new TickEvent
                    {
                        Time = time,
                        StockId = stockId,
                        Price = price,
                        Volume = volume,
                        Vwap = vwap,
                        DayHigh = dayHigh
                    });
                }
            }

            return ticks;
        }

        #endregion

        #region Step 4: Tick Processing

        private List<TimeSnapshot> ProcessTicks(List<TickEvent> mergedTicks)
        {
            var snapshots = new List<TimeSnapshot>();
            var marketOpen = new TimeSpan(9, 0, 0);
            int lastSecond = -1;

            for (int i = 0; i < mergedTicks.Count; i++)
            {
                var tick = mergedTicks[i];

                // Update stock state
                if (_stockStates.TryGetValue(tick.StockId, out var state))
                {
                    state.LastPrice = tick.Price;
                    if (tick.Vwap > 0) state.Vwap = tick.Vwap;
                    if (tick.DayHigh > 0) state.DayHigh = tick.DayHigh;
                    state.CumulativeVolume += tick.Volume;
                    state.CumulativeValue += tick.Price * tick.Volume;
                }

                // Check if we've moved to a new second
                int currentSecond = (int)(tick.Time.TimeOfDay - marketOpen).TotalSeconds;
                if (currentSecond < 0) currentSecond = 0;

                if (currentSecond > lastSecond)
                {
                    var snapshot = CreateSnapshot(currentSecond, tick.Time.TimeOfDay);
                    snapshots.Add(snapshot);
                    lastSecond = currentSecond;
                }
            }

            return snapshots;
        }

        private TimeSnapshot CreateSnapshot(int totalSeconds, TimeSpan timeOfDay)
        {
            var validGroups = new List<GroupSnapshot>();

            foreach (var kvp in _groupMembers)
            {
                string groupName = kvp.Key;
                var memberIds = kvp.Value;

                // Collect valid members (AvgAmount20d >= threshold AND LastPrice > 0)
                var validMembers = new List<(StockLiveState state, string stockId)>();
                foreach (var stockId in memberIds)
                {
                    if (!_stockStates.TryGetValue(stockId, out var state)) continue;
                    if (state.AvgAmount20d < _memberMinMonthVal) continue;
                    if (state.LastPrice <= 0) continue;
                    validMembers.Add((state, stockId));
                }

                if (validMembers.Count == 0) continue;

                // Calculate totalMonthlyVal
                double totalMonthlyVal = 0;
                foreach (var (state, _) in validMembers)
                    totalMonthlyVal += state.AvgAmount20d;

                // Calculate avg price change
                double sumPctChg = 0;
                double sumWeight = 0;
                foreach (var (state, _) in validMembers)
                {
                    if (_isWeightedAvg)
                    {
                        // Match StrongGroupScreener: weight by MonthlyAvgTradingValue (AvgAmount20d)
                        double weight = state.AvgAmount20d;
                        sumPctChg += state.PriceChangePct * weight;
                        sumWeight += weight;
                    }
                    else
                    {
                        sumPctChg += state.PriceChangePct;
                        sumWeight += 1;
                    }
                }
                double avgPriceChangePct = sumWeight > 0 ? sumPctChg / sumWeight : 0;

                // Get valRatio from CSV pre-computed value
                double valRatio = 0;
                _groupValRatios.TryGetValue(groupName, out valRatio);

                // Check validity
                bool isValid = totalMonthlyVal >= _groupMinMonthVal
                    && avgPriceChangePct > _groupMinAvgPctChg
                    && valRatio >= _groupMinValRatio
                    && validMembers.Count > 0;

                if (!isValid) continue;

                // Build member snapshots
                var memberSnapshots = new List<MemberSnapshot>();
                foreach (var (state, stockId) in validMembers)
                {
                    memberSnapshots.Add(new MemberSnapshot
                    {
                        StockId = stockId,
                        StockName = state.StockName,
                        PriceChangePct = state.PriceChangePct,
                        LastPrice = state.LastPrice,
                        PrevClose = state.PrevClose,
                        Vwap = state.Vwap,
                        DayHigh = state.DayHigh,
                        CumulativeValue = state.CumulativeValue,
                        AvgAmount20d = state.AvgAmount20d
                    });
                }

                validGroups.Add(new GroupSnapshot
                {
                    GroupName = groupName,
                    AvgPriceChangePct = avgPriceChangePct,
                    ValRatio = valRatio,
                    TotalMonthlyVal = totalMonthlyVal,
                    ValidMemberCount = validMembers.Count,
                    IsValid = true,
                    Members = memberSnapshots
                });
            }

            // Sort valid groups by AvgPriceChangePct descending, assign rank
            validGroups.Sort((a, b) => b.AvgPriceChangePct.CompareTo(a.AvgPriceChangePct));
            for (int i = 0; i < validGroups.Count; i++)
                validGroups[i].Rank = i + 1;

            // For top N groups: rank members and select
            var selectedStockIds = new HashSet<string>();
            foreach (var group in validGroups)
            {
                if (group.Rank > _groupValidTopN) continue;

                // Sort members by PriceChangePct descending, assign RankInGroup
                group.Members.Sort((a, b) => b.PriceChangePct.CompareTo(a.PriceChangePct));
                for (int i = 0; i < group.Members.Count; i++)
                    group.Members[i].RankInGroup = i + 1;

                // Select top members
                int maxSelect = group.Rank <= _topGroupRankThreshold
                    ? _topGroupMaxSelect
                    : _normalGroupMaxSelect;

                foreach (var member in group.Members)
                {
                    if (member.RankInGroup <= maxSelect && member.PriceChangePct >= _entryMinVwapPctChg)
                    {
                        member.IsSelected = true;
                        selectedStockIds.Add(member.StockId);
                    }
                }
            }

            // Format time string
            int hours = 9 + totalSeconds / 3600;
            int minutes = (totalSeconds % 3600) / 60;
            int seconds = totalSeconds % 60;
            string timeStr = $"{hours:D2}:{minutes:D2}:{seconds:D2}";

            return new TimeSnapshot
            {
                Time = timeStr,
                TotalSeconds = totalSeconds,
                Groups = validGroups,
                ValidGroupCount = validGroups.Count,
                TotalSelectedStocks = selectedStockIds.Count
            };
        }

        #endregion

        #region Config Dictionary Builder

        private Dictionary<string, object> BuildConfigDict()
        {
            return new Dictionary<string, object>
            {
                ["member_min_month_trading_val"] = _memberMinMonthVal,
                ["group_min_month_trading_val"] = _groupMinMonthVal,
                ["group_min_avg_pct_chg"] = _groupMinAvgPctChg,
                ["group_min_val_ratio"] = _groupMinValRatio,
                ["is_weighted_avg"] = _isWeightedAvg,
                ["group_valid_top_n"] = _groupValidTopN,
                ["top_group_rank_threshold"] = _topGroupRankThreshold,
                ["top_group_max_select"] = _topGroupMaxSelect,
                ["normal_group_max_select"] = _normalGroupMaxSelect,
                ["entry_min_vwap_pct_chg"] = _entryMinVwapPctChg
            };
        }

        #endregion

        #region Internal Screening Entry

        private class ScreeningEntry
        {
            public string Date = "";
            public string StockId = "";
            public string StockName = "";
            public string Category = "";
            public double AvgAmount20d;
            public double TodayAmount;
            public double GroupMa20Sum;
            public double GroupTodaySum;
            public double ValRatio;
        }

        #endregion

        #region Parquet Column Helpers

        private static int FindColumn(List<string> fieldNames, params string[] candidates)
        {
            foreach (var c in candidates)
            {
                int idx = fieldNames.IndexOf(c);
                if (idx >= 0) return idx;
            }
            return -1;
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

        private static DateTime GetDateTimeFromColumn(DataColumn col, int row)
        {
            var data = col.Data;
            if (data is DateTimeOffset[] dto) return dto[row].DateTime;
            if (data is DateTime[] dt) return dt[row];
            if (data is long[] longs)
            {
                long val = longs[row];
                if (val > 1e16) return new DateTime(1970, 1, 1).AddTicks(val / 100); // nanoseconds
                if (val > 1e13) return new DateTime(1970, 1, 1).AddTicks(val * 10);  // microseconds
                return new DateTime(1970, 1, 1).AddMilliseconds(val);                // milliseconds
            }
            return DateTime.MinValue;
        }

        private static double GetDoubleFromColumn(DataColumn col, int row)
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

        private static string GetStringFromColumn(DataColumn col, int row)
        {
            var data = col.Data;
            if (data is string[] s) return s[row] ?? "";
            if (data is byte[][] b) return Encoding.UTF8.GetString(b[row] ?? Array.Empty<byte>());
            return data.GetValue(row)?.ToString() ?? "";
        }

        #endregion

        #region Config Value Helpers

        private static double GetDouble(Dictionary<string, object> config, string key, double defaultVal)
        {
            if (config.TryGetValue(key, out var v) && v != null)
            {
                if (v is double dv) return dv;
                if (v is float fv) return fv;
                if (v is int iv) return iv;
                if (v is long lv) return lv;
                if (double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                    return p;
            }
            return defaultVal;
        }

        private static int GetInt(Dictionary<string, object> config, string key, int defaultVal)
        {
            if (config.TryGetValue(key, out var v) && v != null)
            {
                if (v is int iv) return iv;
                if (v is long lv) return (int)lv;
                if (v is double dv) return (int)dv;
                if (int.TryParse(v.ToString(), out var p)) return p;
            }
            return defaultVal;
        }

        private static bool GetBool(Dictionary<string, object> config, string key, bool defaultVal)
        {
            if (config.TryGetValue(key, out var v) && v != null)
            {
                if (v is bool bv) return bv;
                string s = v.ToString()?.Trim().ToLowerInvariant() ?? "";
                if (s == "true" || s == "yes" || s == "1") return true;
                if (s == "false" || s == "no" || s == "0") return false;
            }
            return defaultVal;
        }

        #endregion
    }
}
