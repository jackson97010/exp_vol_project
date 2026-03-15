using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BacktestModule.Core.Models;

namespace BacktestModule.Strategy
{
    /// <summary>
    /// Data processor: loads parquet feature data, processes trade/depth data,
    /// calculates tick sizes and limit prices.
    /// Note: Parquet reading requires Apache.Arrow or Parquet.Net NuGet package.
    /// This implementation provides the structural skeleton; actual parquet I/O
    /// uses placeholder methods that should be backed by a concrete parquet reader.
    /// </summary>
    public class DataProcessor
    {
        private readonly Dictionary<string, object> _config;
        private Dictionary<string, string> _companyNameCache;

        public DataProcessor(Dictionary<string, object> config)
        {
            _config = config;
            _companyNameCache = null;
        }

        /// <summary>
        /// Loads feature data from parquet file for a given stock and date.
        /// Path: D:/feature_data/feature/{date}/{stockId}.parquet
        /// Converts time column, filters 09:00-13:30.
        /// </summary>
        public List<TickData> LoadFeatureData(string stockId, string date)
        {
            string featurePath = $@"D:\feature_data\feature\{date}\{stockId}.parquet";

            if (!File.Exists(featurePath))
            {
                System.Console.WriteLine($"[ERROR] File not found: {featurePath}");
                return new List<TickData>();
            }

            System.Console.WriteLine($"[INFO] Loading data: {featurePath}");

            // Load from parquet (requires Parquet.Net or Apache.Arrow)
            var data = ParquetHelper.ReadParquetToTickData(featurePath);

            // No need to fix dates anymore - timestamps should be correctly parsed now
            // Just verify the date matches what we expect
            var targetDate = DateTime.Parse(date);
            // Optional: Add validation to ensure data is from the correct date

            // Debug: Show first few records to check time parsing
            if (data.Count > 0)
            {
                System.Console.WriteLine($"[DEBUG] First record time: {data[0].Time:yyyy-MM-dd HH:mm:ss.fff}");
                System.Console.WriteLine($"[DEBUG] Last record time: {data[data.Count - 1].Time:yyyy-MM-dd HH:mm:ss.fff}");
            }

            // Filter trading hours: 09:00 - 13:30
            var tradingStart = new TimeSpan(9, 0, 0);
            var tradingEnd = new TimeSpan(13, 30, 0);
            data = data.Where(t => t.Time.TimeOfDay >= tradingStart && t.Time.TimeOfDay <= tradingEnd).ToList();

            System.Console.WriteLine($"[INFO] Loaded {data.Count} records after time filtering");
            return data;
        }

        /// <summary>
        /// Processes trade data: separates Trade/Depth, forward-fills order book columns,
        /// keeps only Trade rows, computes 5-level bid/ask totals.
        /// </summary>
        public List<TickData> ProcessTradeData(List<TickData> rawData)
        {
            if (rawData == null || rawData.Count == 0)
                return new List<TickData>();

            // Check if 'type' column exists meaningfully
            bool hasTypeColumn = rawData.Any(r => !string.IsNullOrEmpty(r.Type));
            if (!hasTypeColumn)
            {
                System.Console.WriteLine("[WARNING] No 'type' column found, assuming all data is Trade.");
                return rawData;
            }

            var tradeRows = rawData.Where(r => r.Type == "Trade").ToList();
            var depthRows = rawData.Where(r => r.Type == "Depth").ToList();

            System.Console.WriteLine($"[INFO] Trade rows: {tradeRows.Count}");
            System.Console.WriteLine($"[INFO] Depth rows: {depthRows.Count}");

            if (tradeRows.Count == 0)
            {
                System.Console.WriteLine("[WARNING] No Trade data.");
                return new List<TickData>();
            }

            // Set order book columns in Trade rows to NaN (represented as 0 -> NaN)
            foreach (var row in tradeRows)
            {
                SetOrderBookToNaN(row);
            }

            // Merge trade + depth, sort by time
            var merged = new List<TickData>(tradeRows.Count + depthRows.Count);
            merged.AddRange(tradeRows);
            merged.AddRange(depthRows);
            merged = merged.OrderBy(t => t.Time).ToList();

            // Forward-fill order book columns
            ForwardFillOrderBook(merged);

            // Keep only Trade rows
            var result = merged.Where(r => r.Type == "Trade").ToList();

            // Compute bid_volume_5level and ask_volume_5level if missing
            foreach (var row in result)
            {
                EnsureFiveLevelTotals(row);
            }

            System.Console.WriteLine($"[INFO] Processed Trade rows: {result.Count}");
            return result;
        }

        /// <summary>
        /// Alternative merge method (same logic as ProcessTradeData).
        /// </summary>
        public List<TickData> MergeDepthData(List<TickData> tradeData, List<TickData> depthData)
        {
            foreach (var row in tradeData)
                SetOrderBookToNaN(row);

            var merged = new List<TickData>(tradeData.Count + depthData.Count);
            merged.AddRange(tradeData);
            merged.AddRange(depthData);
            merged = merged.OrderBy(t => t.Time).ToList();

            ForwardFillOrderBook(merged);

            var result = merged.Where(r => r.Type == "Trade").ToList();
            foreach (var row in result)
                EnsureFiveLevelTotals(row);

            return result;
        }

        /// <summary>
        /// Gets the previous trading day's close price for a stock.
        /// </summary>
        public double? GetReferencePrice(string stockId, string date, string closePath = null)
        {
            // Try CSV first, fallback to parquet if not found
            string csvPath = @"D:\03_預估量相關資量\CSharp\BacktestModule\close_prices.csv";
            if (File.Exists(csvPath))
            {
                return GetReferencePriceFromCsv(stockId, date, csvPath);
            }

            // Fallback to parquet (prefer _compat version for Parquet.Net 4.x compatibility)
            if (closePath == null)
            {
                string compatPath = @"D:\03_預估量相關資量\CSharp\BacktestModule\close_compat.parquet";
                if (File.Exists(compatPath))
                    closePath = compatPath;
                else
                    closePath = @"C:\Users\User\Documents\_02_bt\Backtest_tick_module\close.parquet";
            }

            if (!File.Exists(closePath))
            {
                System.Console.WriteLine($"[WARNING] Close price file not found: {closePath}");
                return null;
            }

            try
            {
                // Read close.parquet: index=dates, columns=stock_ids, values=close prices
                var closeData = ParquetHelper.ReadCloseParquet(closePath);
                var currentDate = DateTime.Parse(date);

                // Find the last trading day before currentDate
                var prevDates = closeData.Keys.Where(d => d < currentDate).OrderBy(d => d).ToList();
                if (prevDates.Count == 0)
                {
                    System.Console.WriteLine($"[WARNING] No previous close price found for {date}");
                    return null;
                }

                var prevDate = prevDates.Last();
                if (closeData[prevDate].TryGetValue(stockId, out double refPrice))
                {
                    System.Console.WriteLine($"[INFO] Previous close: {refPrice:F2} (date: {prevDate:yyyy-MM-dd})");
                    return refPrice;
                }
                else
                {
                    System.Console.WriteLine($"[WARNING] Stock {stockId} not found in close.parquet");
                    return null;
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[ERROR] Failed to read close.parquet: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets the reference price from CSV file.
        /// </summary>
        private double? GetReferencePriceFromCsv(string stockId, string date, string csvPath)
        {
            try
            {
                System.Console.WriteLine($"[INFO] Reading close prices from CSV: {csvPath}");

                var lines = File.ReadAllLines(csvPath);
                if (lines.Length < 2) // Must have header and at least one row
                {
                    System.Console.WriteLine($"[WARNING] CSV file is empty");
                    return null;
                }

                // Parse header to find stock column index
                var headers = lines[0].Split(',');
                int stockColumnIndex = -1;
                for (int i = 0; i < headers.Length; i++)
                {
                    if (headers[i] == stockId)
                    {
                        stockColumnIndex = i;
                        break;
                    }
                }

                if (stockColumnIndex == -1)
                {
                    System.Console.WriteLine($"[WARNING] Stock {stockId} not found in CSV columns");
                    return null;
                }

                // Find the last trading day before current date
                var currentDate = DateTime.Parse(date);
                DateTime? lastValidDate = null;
                double? lastValidPrice = null;

                for (int i = 1; i < lines.Length; i++) // Skip header
                {
                    var values = lines[i].Split(',');
                    if (values.Length > stockColumnIndex)
                    {
                        var rowDate = DateTime.Parse(values[0]); // Date is first column
                        if (rowDate < currentDate)
                        {
                            var priceStr = values[stockColumnIndex];
                            if (double.TryParse(priceStr, out double price) && price > 0)
                            {
                                lastValidDate = rowDate;
                                lastValidPrice = price;
                            }
                        }
                    }
                }

                if (lastValidPrice.HasValue && lastValidDate.HasValue)
                {
                    System.Console.WriteLine($"[INFO] Previous close: {lastValidPrice.Value:F2} (date: {lastValidDate.Value:yyyy-MM-dd})");
                    return lastValidPrice.Value;
                }
                else
                {
                    System.Console.WriteLine($"[WARNING] No valid close price found for {stockId} before {date}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[ERROR] Failed to read CSV: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Calculates limit-up price. Delegates to TickSizeHelper.
        /// </summary>
        public static double CalculateLimitUp(double previousClose) => TickSizeHelper.CalculateLimitUp(previousClose);

        /// <summary>
        /// Calculates limit-down price. Delegates to TickSizeHelper.
        /// </summary>
        public static double CalculateLimitDown(double previousClose) => TickSizeHelper.CalculateLimitDown(previousClose);

        /// <summary>
        /// Gets the tick size as decimal for a given price.
        /// </summary>
        public static decimal GetTickSizeDecimal(double price) => TickSizeHelper.GetTickSizeDecimal(price);

        /// <summary>
        /// Adds placeholder computed columns to tick data (filled during backtest).
        /// </summary>
        public List<TickData> AddCalculatedColumns(List<TickData> data)
        {
            foreach (var row in data)
            {
                row.DayHighGrowthRate = 0.0;
                row.BidAvgVolume = 0.0;
                row.AskAvgVolume = 0.0;
                row.BalanceRatio = 0.0;
                row.DayHighBreakout = false;
            }
            return data;
        }

        /// <summary>
        /// Gets the company name for a stock ID.
        /// </summary>
        public string GetCompanyName(string stockId)
        {
            try
            {
                if (_companyNameCache == null)
                {
                    var possiblePaths = new[]
                    {
                        "company_basic_info_compat.parquet",
                        @"D:\feature_data\company_basic_info_compat.parquet",
                        @"C:\Users\User\Documents\_02_bt\Backtest_tick_module\company_basic_info_compat.parquet",
                        "company_basic_info.parquet",
                        @"D:\feature_data\company_basic_info.parquet",
                        @"C:\Users\User\Documents\_02_bt\Backtest_tick_module\company_basic_info.parquet"
                    };

                    foreach (var path in possiblePaths)
                    {
                        if (File.Exists(path))
                        {
                            _companyNameCache = ParquetHelper.ReadCompanyInfo(path);
                            System.Console.WriteLine($"[INFO] Loaded company info from: {path}");
                            break;
                        }
                    }

                    _companyNameCache ??= new Dictionary<string, string>();
                }

                return _companyNameCache.TryGetValue(stockId, out var name) ? name : stockId;
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[WARNING] Failed to load company name: {ex.Message}");
                return stockId;
            }
        }

        /// <summary>
        /// Filters data to trading hours (09:00-13:30).
        /// </summary>
        public List<TickData> FilterTradingHours(List<TickData> data)
        {
            var start = new TimeSpan(9, 0, 0);
            var end = new TimeSpan(13, 30, 0);
            return data.Where(t => t.Time.TimeOfDay >= start && t.Time.TimeOfDay <= end).ToList();
        }

        /// <summary>
        /// Validates that data has required columns and is non-empty.
        /// </summary>
        public bool ValidateData(List<TickData> data)
        {
            if (data == null || data.Count == 0)
            {
                System.Console.WriteLine("[ERROR] Data is empty");
                return false;
            }

            // Check time ordering
            for (int i = 1; i < data.Count; i++)
            {
                if (data[i].Time < data[i - 1].Time)
                {
                    System.Console.WriteLine("[WARNING] Data not sorted by time, sorting now.");
                    data.Sort((a, b) => a.Time.CompareTo(b.Time));
                    break;
                }
            }

            return true;
        }

        // ===== Private helpers =====

        private static void SetOrderBookToNaN(TickData row)
        {
            // Replace 0 with NaN for order book columns (so they can be forward-filled)
            if (row.Bid1Volume == 0) row.Bid1Volume = double.NaN;
            if (row.Bid2Volume == 0) row.Bid2Volume = double.NaN;
            if (row.Bid3Volume == 0) row.Bid3Volume = double.NaN;
            if (row.Bid4Volume == 0) row.Bid4Volume = double.NaN;
            if (row.Bid5Volume == 0) row.Bid5Volume = double.NaN;
            if (row.Ask1Volume == 0) row.Ask1Volume = double.NaN;
            if (row.Ask2Volume == 0) row.Ask2Volume = double.NaN;
            if (row.Ask3Volume == 0) row.Ask3Volume = double.NaN;
            if (row.Ask4Volume == 0) row.Ask4Volume = double.NaN;
            if (row.Ask5Volume == 0) row.Ask5Volume = double.NaN;
            if (row.BidVolume5Level == 0) row.BidVolume5Level = double.NaN;
            if (row.AskVolume5Level == 0) row.AskVolume5Level = double.NaN;
            if (row.BidAskRatio == 0) row.BidAskRatio = double.NaN;
        }

        private static void ForwardFillOrderBook(List<TickData> data)
        {
            for (int i = 1; i < data.Count; i++)
            {
                var prev = data[i - 1];
                var curr = data[i];

                if (double.IsNaN(curr.Bid1Volume)) curr.Bid1Volume = prev.Bid1Volume;
                if (double.IsNaN(curr.Bid2Volume)) curr.Bid2Volume = prev.Bid2Volume;
                if (double.IsNaN(curr.Bid3Volume)) curr.Bid3Volume = prev.Bid3Volume;
                if (double.IsNaN(curr.Bid4Volume)) curr.Bid4Volume = prev.Bid4Volume;
                if (double.IsNaN(curr.Bid5Volume)) curr.Bid5Volume = prev.Bid5Volume;
                if (double.IsNaN(curr.Ask1Volume)) curr.Ask1Volume = prev.Ask1Volume;
                if (double.IsNaN(curr.Ask2Volume)) curr.Ask2Volume = prev.Ask2Volume;
                if (double.IsNaN(curr.Ask3Volume)) curr.Ask3Volume = prev.Ask3Volume;
                if (double.IsNaN(curr.Ask4Volume)) curr.Ask4Volume = prev.Ask4Volume;
                if (double.IsNaN(curr.Ask5Volume)) curr.Ask5Volume = prev.Ask5Volume;
                if (double.IsNaN(curr.BidVolume5Level)) curr.BidVolume5Level = prev.BidVolume5Level;
                if (double.IsNaN(curr.AskVolume5Level)) curr.AskVolume5Level = prev.AskVolume5Level;
                if (double.IsNaN(curr.BidAskRatio)) curr.BidAskRatio = prev.BidAskRatio;
            }
        }

        private static void EnsureFiveLevelTotals(TickData row)
        {
            // Compute bid_volume_5level if missing or NaN
            if (double.IsNaN(row.BidVolume5Level) || row.BidVolume5Level == 0)
            {
                double sum = 0;
                int count = 0;
                foreach (var v in new[] { row.Bid1Volume, row.Bid2Volume, row.Bid3Volume, row.Bid4Volume, row.Bid5Volume })
                {
                    if (!double.IsNaN(v)) { sum += v; count++; }
                }
                if (count > 0) row.BidVolume5Level = sum;
            }

            // Compute ask_volume_5level if missing or NaN
            if (double.IsNaN(row.AskVolume5Level) || row.AskVolume5Level == 0)
            {
                double sum = 0;
                int count = 0;
                foreach (var v in new[] { row.Ask1Volume, row.Ask2Volume, row.Ask3Volume, row.Ask4Volume, row.Ask5Volume })
                {
                    if (!double.IsNaN(v)) { sum += v; count++; }
                }
                if (count > 0) row.AskVolume5Level = sum;
            }
        }
    }
}
