using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet;
using BacktestModule.Core.Models;

namespace BacktestModule.Strategy
{
    /// <summary>
    /// Helper class for reading parquet files using Parquet.Net library.
    /// Uses synchronous wrappers around async Parquet.Net APIs.
    /// </summary>
    public static class ParquetHelper
    {
        /// <summary>
        /// Reads a feature parquet file and returns a list of TickData.
        /// </summary>
        public static List<TickData> ReadParquetToTickData(string path)
        {
            if (!File.Exists(path))
            {
                System.Console.WriteLine($"[ERROR] File not found: {path}");
                return new List<TickData>();
            }

            try
            {
                System.Console.WriteLine($"[INFO] Reading parquet file: {path}");

                var tickDataList = ReadParquetAsync(path).GetAwaiter().GetResult();

                System.Console.WriteLine($"[INFO] Successfully read {tickDataList.Count} records from parquet file");
                return tickDataList;
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[ERROR] Failed to read parquet file: {ex.Message}");
                if (ex.InnerException != null)
                {
                    System.Console.WriteLine($"[ERROR] Inner exception: {ex.InnerException.Message}");
                }
                return new List<TickData>();
            }
        }

        // Column projection: only read these columns from parquet (case-insensitive)
        private static readonly HashSet<string> RequiredColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "time", "datetime", "price", "close", "volume", "vol",
            "tick_type", "tick_flag", "type", "trade_type",
            "day_high", "high",
            "bid1_volume", "bid2_volume", "bid3_volume", "bid4_volume", "bid5_volume",
            "bid_volume1", "bid_volume2", "bid_volume3", "bid_volume4", "bid_volume5",
            "ask1_volume", "ask2_volume", "ask3_volume", "ask4_volume", "ask5_volume",
            "ask_volume1", "ask_volume2", "ask_volume3", "ask_volume4", "ask_volume5",
            "bid_volume_5level", "ask_volume_5level", "bid_ask_ratio",
            "ratio_15s_300s", "ratio_15s_180s_w321",
            "pct_2min", "pct_3min", "pct_5min",
            "low_1m", "low_3m", "low_5m", "low_7m", "low_10m", "low_15m",
            "high_3m",
            "vwap", "vwap_5m"
        };

        // Push-down time filter: trading session 09:00 - 13:30
        private static readonly TimeSpan TradingStart = new TimeSpan(9, 0, 0);
        private static readonly TimeSpan TradingEnd = new TimeSpan(13, 30, 0);

        private static async Task<List<TickData>> ReadParquetAsync(string path)
        {
            var tickDataList = new List<TickData>();

            using (Stream fileStream = File.OpenRead(path))
            {
                using (var parquetReader = await ParquetReader.CreateAsync(fileStream))
                {
                    var schema = parquetReader.Schema;
                    var dataFields = schema.GetDataFields().ToList();

                    // Identify which fields to read (column projection)
                    var fieldsToRead = new List<(Parquet.Schema.DataField field, string lowerName)>();
                    foreach (var field in dataFields)
                    {
                        if (RequiredColumns.Contains(field.Name))
                        {
                            fieldsToRead.Add((field, field.Name.ToLower()));
                        }
                    }

                    if (fieldsToRead.Count == 0)
                        return tickDataList;

                    // Read row-group by row-group, column by column
                    for (int rg = 0; rg < parquetReader.RowGroupCount; rg++)
                    {
                        using (var rgReader = parquetReader.OpenRowGroupReader(rg))
                        {
                            // Read only required columns
                            var columnArrays = new Dictionary<string, Array>(fieldsToRead.Count, StringComparer.Ordinal);
                            foreach (var (field, lowerName) in fieldsToRead)
                            {
                                var col = await rgReader.ReadColumnAsync(field);
                                var arr = col.Data as Array;
                                if (arr != null)
                                    columnArrays[lowerName] = arr;
                            }

                            if (columnArrays.Count == 0)
                                continue;

                            // Determine row count from first available array
                            int rowCount = columnArrays.Values.First().Length;

                            // Pre-resolve column arrays once (avoid repeated dictionary lookups per row)
                            Array timeArr = ResolveArray(columnArrays, "time", "datetime");
                            Array priceArr = ResolveArray(columnArrays, "price", "close");
                            Array volumeArr = ResolveArray(columnArrays, "volume", "vol");
                            Array tickTypeArr = ResolveArray(columnArrays, "tick_type", "tick_flag");
                            Array typeArr = ResolveArray(columnArrays, "type", "trade_type");
                            Array dayHighArr = ResolveArray(columnArrays, "day_high", "high");
                            Array bidAskRatioArr = ResolveArray(columnArrays, "bid_ask_ratio");
                            Array bid1Arr = ResolveArray(columnArrays, "bid1_volume", "bid_volume1");
                            Array bid2Arr = ResolveArray(columnArrays, "bid2_volume", "bid_volume2");
                            Array bid3Arr = ResolveArray(columnArrays, "bid3_volume", "bid_volume3");
                            Array bid4Arr = ResolveArray(columnArrays, "bid4_volume", "bid_volume4");
                            Array bid5Arr = ResolveArray(columnArrays, "bid5_volume", "bid_volume5");
                            Array ask1Arr = ResolveArray(columnArrays, "ask1_volume", "ask_volume1");
                            Array ask2Arr = ResolveArray(columnArrays, "ask2_volume", "ask_volume2");
                            Array ask3Arr = ResolveArray(columnArrays, "ask3_volume", "ask_volume3");
                            Array ask4Arr = ResolveArray(columnArrays, "ask4_volume", "ask_volume4");
                            Array ask5Arr = ResolveArray(columnArrays, "ask5_volume", "ask_volume5");
                            Array bidVol5Arr = ResolveArray(columnArrays, "bid_volume_5level");
                            Array askVol5Arr = ResolveArray(columnArrays, "ask_volume_5level");
                            Array ratio15s300sArr = ResolveArray(columnArrays, "ratio_15s_300s");
                            Array ratio15s180sArr = ResolveArray(columnArrays, "ratio_15s_180s_w321");
                            Array pct2Arr = ResolveArray(columnArrays, "pct_2min");
                            Array pct3Arr = ResolveArray(columnArrays, "pct_3min");
                            Array pct5Arr = ResolveArray(columnArrays, "pct_5min");
                            Array low1mArr = ResolveArray(columnArrays, "low_1m");
                            Array low3mArr = ResolveArray(columnArrays, "low_3m");
                            Array low5mArr = ResolveArray(columnArrays, "low_5m");
                            Array low7mArr = ResolveArray(columnArrays, "low_7m");
                            Array low10mArr = ResolveArray(columnArrays, "low_10m");
                            Array low15mArr = ResolveArray(columnArrays, "low_15m");
                            Array high3mArr = ResolveArray(columnArrays, "high_3m");
                            Array vwapArr = ResolveArray(columnArrays, "vwap");
                            Array vwap5mArr = ResolveArray(columnArrays, "vwap_5m");

                            // Build TickData row by row with push-down time filter
                            for (int i = 0; i < rowCount; i++)
                            {
                                // Parse time first for push-down filter
                                var time = GetDateTimeFromValue(timeArr?.GetValue(i));
                                if (time == DateTime.MinValue)
                                    continue;

                                // Push-down time filter: skip pre-market / post-market data
                                var tod = time.TimeOfDay;
                                if (tod < TradingStart || tod > TradingEnd)
                                    continue;

                                var tick = new TickData
                                {
                                    Time = time,
                                    Price = GetDoubleFromValue(priceArr?.GetValue(i)),
                                    Volume = GetDoubleFromValue(volumeArr?.GetValue(i)),
                                    TickType = GetIntFromValue(tickTypeArr?.GetValue(i)),
                                    Type = GetStringFromValue(typeArr?.GetValue(i)) ?? "Trade",
                                    DayHigh = GetDoubleFromValue(dayHighArr?.GetValue(i)),
                                    BidAskRatio = GetDoubleFromValue(bidAskRatioArr?.GetValue(i)),

                                    Bid1Volume = GetDoubleFromValue(bid1Arr?.GetValue(i)),
                                    Bid2Volume = GetDoubleFromValue(bid2Arr?.GetValue(i)),
                                    Bid3Volume = GetDoubleFromValue(bid3Arr?.GetValue(i)),
                                    Bid4Volume = GetDoubleFromValue(bid4Arr?.GetValue(i)),
                                    Bid5Volume = GetDoubleFromValue(bid5Arr?.GetValue(i)),

                                    Ask1Volume = GetDoubleFromValue(ask1Arr?.GetValue(i)),
                                    Ask2Volume = GetDoubleFromValue(ask2Arr?.GetValue(i)),
                                    Ask3Volume = GetDoubleFromValue(ask3Arr?.GetValue(i)),
                                    Ask4Volume = GetDoubleFromValue(ask4Arr?.GetValue(i)),
                                    Ask5Volume = GetDoubleFromValue(ask5Arr?.GetValue(i)),

                                    BidVolume5Level = GetDoubleFromValue(bidVol5Arr?.GetValue(i)),
                                    AskVolume5Level = GetDoubleFromValue(askVol5Arr?.GetValue(i)),

                                    Ratio15s300s = GetDoubleFromValue(ratio15s300sArr?.GetValue(i)),
                                    Ratio15s180sW321 = GetDoubleFromValue(ratio15s180sArr?.GetValue(i)),
                                    Pct2Min = GetDoubleFromValue(pct2Arr?.GetValue(i)),
                                    Pct3Min = GetDoubleFromValue(pct3Arr?.GetValue(i)),
                                    Pct5Min = GetDoubleFromValue(pct5Arr?.GetValue(i)),

                                    Low1m = GetDoubleFromValue(low1mArr?.GetValue(i)),
                                    Low3m = GetDoubleFromValue(low3mArr?.GetValue(i)),
                                    Low5m = GetDoubleFromValue(low5mArr?.GetValue(i)),
                                    Low7m = GetDoubleFromValue(low7mArr?.GetValue(i)),
                                    Low10m = GetDoubleFromValue(low10mArr?.GetValue(i)),
                                    Low15m = GetDoubleFromValue(low15mArr?.GetValue(i)),

                                    High3m = GetDoubleFromValue(high3mArr?.GetValue(i)),

                                    Vwap = GetDoubleFromValue(vwapArr?.GetValue(i)),
                                    Vwap5m = GetDoubleFromValue(vwap5mArr?.GetValue(i))
                                };

                                tickDataList.Add(tick);
                            }
                        }
                    }
                }
            }

            return tickDataList;
        }

        /// <summary>
        /// Resolves a column array by primary name, falling back to an alias.
        /// Returns null if neither name exists in the dictionary.
        /// </summary>
        private static Array ResolveArray(Dictionary<string, Array> cols, string name1, string name2 = null)
        {
            if (cols.TryGetValue(name1, out var arr1)) return arr1;
            if (name2 != null && cols.TryGetValue(name2, out var arr2)) return arr2;
            return null;
        }

        /// <summary>
        /// Reads close.parquet: returns Dictionary<DateTime, Dictionary<string, double>>
        /// where outer key is date, inner key is stock_id, value is close price.
        /// </summary>
        public static Dictionary<DateTime, Dictionary<string, double>> ReadCloseParquet(string path)
        {
            if (!File.Exists(path))
            {
                System.Console.WriteLine($"[WARNING] Close parquet file not found: {path}");
                return new Dictionary<DateTime, Dictionary<string, double>>();
            }

            try
            {
                System.Console.WriteLine($"[INFO] Reading close parquet: {path}");

                var result = ReadCloseParquetAsync(path).GetAwaiter().GetResult();

                System.Console.WriteLine($"[INFO] Successfully read close prices for {result.Count} dates");
                return result;
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[ERROR] Failed to read close parquet: {ex.Message}");
                return new Dictionary<DateTime, Dictionary<string, double>>();
            }
        }

        private static async Task<Dictionary<DateTime, Dictionary<string, double>>> ReadCloseParquetAsync(string path)
        {
            var result = new Dictionary<DateTime, Dictionary<string, double>>();

            try
            {
                using (Stream fileStream = File.OpenRead(path))
                {
                    using (var parquetReader = await ParquetReader.CreateAsync(fileStream))
                    {
                        // Read the parquet file in a different way to handle pandas index
                        // Get the schema first
                        var schema = parquetReader.Schema;
                        var dataFields = schema.GetDataFields().ToList();

                        System.Console.WriteLine($"[DEBUG] Close parquet has {dataFields.Count} fields");

                        // Read group by group instead of using ReadAsTableAsync
                        for (int i = 0; i < parquetReader.RowGroupCount; i++)
                        {
                            using (var rowGroupReader = parquetReader.OpenRowGroupReader(i))
                            {
                                // Read the date column (should be the first column named 'date' or similar)
                                var dateColumn = await rowGroupReader.ReadColumnAsync(dataFields[0]);
                                var dates = new List<DateTime>();

                                // Convert date values
                                var dateArray = dateColumn.Data as Array;
                                if (dateArray != null)
                                {
                                    foreach (var dateValue in dateArray)
                                    {
                                        dates.Add(GetDateTimeFromValue(dateValue));
                                    }
                                }

                                // Read each stock column
                                for (int colIdx = 1; colIdx < dataFields.Count; colIdx++)
                                {
                                    string stockId = dataFields[colIdx].Name;
                                    var priceColumn = await rowGroupReader.ReadColumnAsync(dataFields[colIdx]);
                                    var priceArray = priceColumn.Data as Array;

                                    if (priceArray != null)
                                    {
                                        // Match dates with prices
                                        int arrayLength = priceArray.Length;
                                        for (int rowIdx = 0; rowIdx < dates.Count && rowIdx < arrayLength; rowIdx++)
                                        {
                                            var date = dates[rowIdx];
                                            var price = GetDoubleFromValue(priceArray.GetValue(rowIdx));

                                            if (price > 0)
                                            {
                                                if (!result.ContainsKey(date))
                                                    result[date] = new Dictionary<string, double>();
                                                result[date][stockId] = price;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        System.Console.WriteLine($"[DEBUG] Successfully read close prices for {result.Count} dates");
                        if (result.Count > 0)
                        {
                            var sortedDates = result.Keys.OrderBy(d => d).ToList();
                            System.Console.WriteLine($"[DEBUG] Date range: {sortedDates.First():yyyy-MM-dd} to {sortedDates.Last():yyyy-MM-dd}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[ERROR] Failed to read close parquet: {ex.Message}");
                if (ex.InnerException != null)
                {
                    System.Console.WriteLine($"[ERROR] Inner exception: {ex.InnerException.Message}");
                }

                // Try alternative approach if the above fails
                return await ReadCloseParquetAlternativeAsync(path);
            }

            return result;
        }

        // Alternative method using table reader (fallback)
        private static async Task<Dictionary<DateTime, Dictionary<string, double>>> ReadCloseParquetAlternativeAsync(string path)
        {
            var result = new Dictionary<DateTime, Dictionary<string, double>>();

            System.Console.WriteLine("[INFO] Trying alternative parquet reading method...");

            try
            {
                using (Stream fileStream = File.OpenRead(path))
                {
                    using (var parquetReader = await ParquetReader.CreateAsync(fileStream))
                    {
                        // Try to read as table but handle the special case
                        var schema = parquetReader.Schema;
                        var dataFields = schema.GetDataFields().ToList();

                        // Check if first field is named 'date' or similar
                        bool hasDateColumn = dataFields[0].Name.ToLower().Contains("date") ||
                                            dataFields[0].Name == "__index_level_0__";

                        var table = await parquetReader.ReadAsTableAsync();

                        if (table == null || table.Count == 0)
                            return result;

                        foreach (var row in table)
                        {
                            DateTime date = DateTime.MinValue;
                            Dictionary<string, double> stockPrices = new Dictionary<string, double>();

                            for (int i = 0; i < dataFields.Count; i++)
                            {
                                if (i == 0 && hasDateColumn)
                                {
                                    // First column is the date
                                    date = GetDateTimeFromValue(row[i]);
                                }
                                else
                                {
                                    // Stock columns
                                    string stockId = dataFields[i].Name;
                                    double price = GetDoubleFromValue(row[i]);
                                    if (price > 0)
                                    {
                                        stockPrices[stockId] = price;
                                    }
                                }
                            }

                            if (date != DateTime.MinValue && stockPrices.Count > 0)
                            {
                                result[date] = stockPrices;
                            }
                        }
                    }
                }
            }
            catch (Exception ex2)
            {
                System.Console.WriteLine($"[ERROR] Alternative method also failed: {ex2.Message}");
            }

            return result;
        }

        /// <summary>
        /// Reads company basic info parquet: returns Dictionary<stockId, companyName>.
        /// </summary>
        public static Dictionary<string, string> ReadCompanyInfo(string path)
        {
            if (!File.Exists(path))
            {
                System.Console.WriteLine($"[WARNING] Company info parquet file not found: {path}");
                return new Dictionary<string, string>();
            }

            try
            {
                System.Console.WriteLine($"[INFO] Reading company info: {path}");

                var result = ReadCompanyInfoAsync(path).GetAwaiter().GetResult();

                System.Console.WriteLine($"[INFO] Successfully read company info for {result.Count} companies");
                return result;
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[ERROR] Failed to read company info: {ex.Message}");
                return new Dictionary<string, string>();
            }
        }

        private static async Task<Dictionary<string, string>> ReadCompanyInfoAsync(string path)
        {
            var result = new Dictionary<string, string>();

            using (Stream fileStream = File.OpenRead(path))
            {
                using (var parquetReader = await ParquetReader.CreateAsync(fileStream))
                {
                    var table = await parquetReader.ReadAsTableAsync();

                    if (table == null || table.Count == 0)
                        return result;

                    // Get schema and create column name to index mapping
                    var schema = table.Schema;
                    var fields = schema.GetDataFields().ToList();
                    var columnIndices = new Dictionary<string, int>();

                    for (int i = 0; i < fields.Count; i++)
                    {
                        columnIndices[fields[i].Name.ToLower()] = i;
                    }

                    // Process each row
                    foreach (var row in table)
                    {
                        string stockId = GetStringValue(row, columnIndices, "stock_id", "stockid", "code");
                        string companyName = GetStringValue(row, columnIndices, "company_name", "companyname", "name");

                        if (!string.IsNullOrEmpty(stockId) && !string.IsNullOrEmpty(companyName))
                            result[stockId] = companyName;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Reads ma5_bias5_threshold.parquet (wide-format pandas DataFrame):
        /// Same format as daily_liquidity_threshold.parquet (date index × stock_id columns).
        /// Values are the MA5 × 1.05 threshold prices.
        /// Returns Dictionary&lt;DateTime, Dictionary&lt;string, double&gt;&gt;
        /// </summary>
        public static Dictionary<DateTime, Dictionary<string, double>> ReadMa5BiasThresholdParquet(string path)
        {
            // Same wide-format as liquidity threshold; reuse the same reader
            return ReadLiquidityThresholdParquet(path);
        }

        /// <summary>
        /// Reads daily_liquidity_threshold.parquet (wide-format pandas DataFrame):
        /// - Row index (first column) = dates (DatetimeIndex)
        /// - Columns = stock IDs (e.g., "2330", "2454")
        /// - Values = base threshold amounts (double)
        /// Returns Dictionary&lt;DateTime, Dictionary&lt;string, double&gt;&gt;
        /// Same structure as ReadCloseParquet since both are wide-format DataFrames.
        /// </summary>
        public static Dictionary<DateTime, Dictionary<string, double>> ReadLiquidityThresholdParquet(string path)
        {
            if (!File.Exists(path))
            {
                System.Console.WriteLine($"[WARNING] Liquidity threshold parquet not found: {path}");
                return new Dictionary<DateTime, Dictionary<string, double>>();
            }

            try
            {
                System.Console.WriteLine($"[INFO] Reading liquidity threshold parquet: {path}");
                var result = ReadLiquidityThresholdAsync(path).GetAwaiter().GetResult();
                System.Console.WriteLine($"[INFO] Successfully read liquidity thresholds for {result.Count} dates");
                return result;
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[ERROR] Failed to read liquidity threshold parquet: {ex.Message}");
                if (ex.InnerException != null)
                {
                    System.Console.WriteLine($"[ERROR] Inner exception: {ex.InnerException.Message}");
                }
                return new Dictionary<DateTime, Dictionary<string, double>>();
            }
        }

        private static async Task<Dictionary<DateTime, Dictionary<string, double>>> ReadLiquidityThresholdAsync(string path)
        {
            var result = new Dictionary<DateTime, Dictionary<string, double>>();

            try
            {
                using (Stream fileStream = File.OpenRead(path))
                {
                    using (var parquetReader = await ParquetReader.CreateAsync(fileStream))
                    {
                        var schema = parquetReader.Schema;
                        var dataFields = schema.GetDataFields().ToList();

                        System.Console.WriteLine($"[DEBUG] Liquidity threshold parquet has {dataFields.Count} fields");

                        // Find date column by name (may be first or last column depending on pandas serialization)
                        int dateColIdx = -1;
                        for (int f = 0; f < dataFields.Count; f++)
                        {
                            string fname = dataFields[f].Name.ToLower();
                            if (fname == "date" || fname == "__index_level_0__")
                            {
                                dateColIdx = f;
                                break;
                            }
                        }

                        if (dateColIdx < 0)
                        {
                            System.Console.WriteLine("[WARNING] Cannot find 'date' column in liquidity threshold parquet, trying first column");
                            dateColIdx = 0;
                        }

                        System.Console.WriteLine($"[DEBUG] Date column: '{dataFields[dateColIdx].Name}' at index {dateColIdx}");

                        // Read group by group
                        for (int i = 0; i < parquetReader.RowGroupCount; i++)
                        {
                            using (var rowGroupReader = parquetReader.OpenRowGroupReader(i))
                            {
                                // Read the date column
                                var dateColumn = await rowGroupReader.ReadColumnAsync(dataFields[dateColIdx]);
                                var dates = new List<DateTime>();

                                var dateArray = dateColumn.Data as Array;
                                if (dateArray != null)
                                {
                                    foreach (var dateValue in dateArray)
                                    {
                                        dates.Add(GetDateTimeFromValue(dateValue));
                                    }
                                }

                                // Read all other columns (stock IDs with threshold values)
                                for (int colIdx = 0; colIdx < dataFields.Count; colIdx++)
                                {
                                    if (colIdx == dateColIdx) continue;  // Skip date column

                                    string stockId = dataFields[colIdx].Name;
                                    var valueColumn = await rowGroupReader.ReadColumnAsync(dataFields[colIdx]);
                                    var valueArray = valueColumn.Data as Array;

                                    if (valueArray != null)
                                    {
                                        int arrayLength = valueArray.Length;
                                        for (int rowIdx = 0; rowIdx < dates.Count && rowIdx < arrayLength; rowIdx++)
                                        {
                                            var date = dates[rowIdx];
                                            var val = valueArray.GetValue(rowIdx);
                                            var threshold = GetDoubleFromValue(val);

                                            // Filter out NaN (parquet null -> GetDoubleFromValue returns 0)
                                            // and actual NaN values
                                            if (threshold > 0 && !double.IsNaN(threshold))
                                            {
                                                if (!result.ContainsKey(date))
                                                    result[date] = new Dictionary<string, double>();
                                                result[date][stockId] = threshold;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        if (result.Count > 0)
                        {
                            var sortedDates = result.Keys.OrderBy(d => d).ToList();
                            int totalStocks = result.Values.SelectMany(d => d.Keys).Distinct().Count();
                            System.Console.WriteLine($"[INFO] Liquidity threshold date range: {sortedDates.First():yyyy-MM-dd} to {sortedDates.Last():yyyy-MM-dd}");
                            System.Console.WriteLine($"[INFO] Liquidity threshold stock count: {totalStocks}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[ERROR] Failed to read liquidity threshold parquet: {ex.Message}");
                if (ex.InnerException != null)
                {
                    System.Console.WriteLine($"[ERROR] Inner exception: {ex.InnerException.Message}");
                }

                // Try alternative approach (table reader fallback)
                return await ReadLiquidityThresholdAlternativeAsync(path);
            }

            return result;
        }

        /// <summary>
        /// Fallback method for reading liquidity threshold parquet using table reader.
        /// Finds the date column by name instead of assuming position.
        /// </summary>
        private static async Task<Dictionary<DateTime, Dictionary<string, double>>> ReadLiquidityThresholdAlternativeAsync(string path)
        {
            var result = new Dictionary<DateTime, Dictionary<string, double>>();

            System.Console.WriteLine("[INFO] Trying alternative liquidity threshold reading method...");

            try
            {
                using (Stream fileStream = File.OpenRead(path))
                {
                    using (var parquetReader = await ParquetReader.CreateAsync(fileStream))
                    {
                        var schema = parquetReader.Schema;
                        var dataFields = schema.GetDataFields().ToList();

                        // Find date column by name
                        int dateColIdx = -1;
                        for (int f = 0; f < dataFields.Count; f++)
                        {
                            string fname = dataFields[f].Name.ToLower();
                            if (fname == "date" || fname == "__index_level_0__")
                            {
                                dateColIdx = f;
                                break;
                            }
                        }
                        if (dateColIdx < 0) dateColIdx = 0;

                        var table = await parquetReader.ReadAsTableAsync();

                        if (table == null || table.Count == 0)
                            return result;

                        foreach (var row in table)
                        {
                            DateTime date = GetDateTimeFromValue(row[dateColIdx]);
                            var stockThresholds = new Dictionary<string, double>();

                            for (int i = 0; i < dataFields.Count; i++)
                            {
                                if (i == dateColIdx) continue;  // Skip date column

                                string stockId = dataFields[i].Name;
                                double threshold = GetDoubleFromValue(row[i]);
                                if (threshold > 0 && !double.IsNaN(threshold))
                                {
                                    stockThresholds[stockId] = threshold;
                                }
                            }

                            if (date != DateTime.MinValue && stockThresholds.Count > 0)
                            {
                                result[date] = stockThresholds;
                            }
                        }
                    }
                }
            }
            catch (Exception ex2)
            {
                System.Console.WriteLine($"[ERROR] Alternative liquidity threshold method also failed: {ex2.Message}");
            }

            return result;
        }

        // Helper methods for extracting values from Row using column indices
        private static DateTime GetDateTimeValue(Parquet.Rows.Row row, Dictionary<string, int> columnIndices, params string[] possibleNames)
        {
            foreach (var name in possibleNames)
            {
                if (columnIndices.TryGetValue(name.ToLower(), out int index))
                {
                    return GetDateTimeFromValue(row[index]);
                }
            }
            return DateTime.MinValue;
        }

        private static DateTime GetDateTimeFromValue(object value)
        {
            try
            {
                if (value == null) return DateTime.MinValue;

                if (value is DateTime dt)
                    return dt;
                if (value is DateTimeOffset dto)
                    return dto.DateTime;
                if (value is long timestamp)
                {
                    // Pandas parquet timestamps are stored as INT64 since 1970-01-01.
                    // The unit can be microseconds (datetime64[us]) or nanoseconds (datetime64[ns]).
                    // IMPORTANT: The timestamps in our parquet files are already in local time (Taiwan UTC+8)
                    // So we should NOT convert from UTC to local time again.
                    var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);

                    // Heuristic to detect unit:
                    // Year 2000 in microseconds since epoch ≈ 9.46e14
                    // Year 2000 in nanoseconds since epoch ≈ 9.46e17
                    // If timestamp > 1e16, it's likely nanoseconds; otherwise microseconds.
                    long ticks;
                    if (Math.Abs(timestamp) > 1e16)
                    {
                        // Nanoseconds: 1 .NET tick = 100 nanoseconds, so divide by 100
                        ticks = timestamp / 100;
                    }
                    else
                    {
                        // Microseconds: 1 microsecond = 10 .NET ticks
                        ticks = timestamp * 10;
                    }

                    var result = epoch.AddTicks(ticks);
                    return result;
                }
                if (value is string str && !string.IsNullOrEmpty(str))
                    return DateTime.Parse(str);
            }
            catch { }
            return DateTime.MinValue;
        }

        private static double GetDoubleValue(Parquet.Rows.Row row, Dictionary<string, int> columnIndices, params string[] possibleNames)
        {
            foreach (var name in possibleNames)
            {
                if (columnIndices.TryGetValue(name.ToLower(), out int index))
                {
                    return GetDoubleFromValue(row[index]);
                }
            }
            return 0.0;
        }

        private static double GetDoubleFromValue(object value)
        {
            try
            {
                if (value == null) return 0.0;

                if (value is double d)
                    return d;
                if (value is float f)
                    return f;
                if (value is decimal dec)
                    return (double)dec;
                if (value is int i)
                    return i;
                if (value is long l)
                    return l;
                if (value is string str && double.TryParse(str, out double parsed))
                    return parsed;
            }
            catch { }
            return 0.0;
        }

        private static int GetIntValue(Parquet.Rows.Row row, Dictionary<string, int> columnIndices, params string[] possibleNames)
        {
            foreach (var name in possibleNames)
            {
                if (columnIndices.TryGetValue(name.ToLower(), out int index))
                {
                    return GetIntFromValue(row[index]);
                }
            }
            return 0;
        }

        private static int GetIntFromValue(object value)
        {
            try
            {
                if (value == null) return 0;

                if (value is int i)
                    return i;
                if (value is long l)
                    return (int)l;
                if (value is double d)
                    return (int)d;
                if (value is float f)
                    return (int)f;
                if (value is string str && int.TryParse(str, out int parsed))
                    return parsed;
            }
            catch { }
            return 0;
        }

        private static string GetStringValue(Parquet.Rows.Row row, Dictionary<string, int> columnIndices, params string[] possibleNames)
        {
            foreach (var name in possibleNames)
            {
                if (columnIndices.TryGetValue(name.ToLower(), out int index))
                {
                    return GetStringFromValue(row[index]);
                }
            }
            return null;
        }

        private static string GetStringFromValue(object value)
        {
            try
            {
                if (value != null)
                    return value.ToString();
            }
            catch { }
            return null;
        }
    }
}