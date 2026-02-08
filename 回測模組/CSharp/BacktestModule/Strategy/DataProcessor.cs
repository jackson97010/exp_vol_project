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
                Console.WriteLine($"[ERROR] File not found: {featurePath}");
                return new List<TickData>();
            }

            Console.WriteLine($"[INFO] Loading data: {featurePath}");

            // Load from parquet (requires Parquet.Net or Apache.Arrow)
            var data = ParquetHelper.ReadParquetToTickData(featurePath);

            // Filter trading hours: 09:00 - 13:30
            var tradingStart = new TimeSpan(9, 0, 0);
            var tradingEnd = new TimeSpan(13, 30, 0);
            data = data.Where(t => t.Time.TimeOfDay >= tradingStart && t.Time.TimeOfDay <= tradingEnd).ToList();

            Console.WriteLine($"[INFO] Loaded {data.Count} records");
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
                Console.WriteLine("[WARNING] No 'type' column found, assuming all data is Trade.");
                return rawData;
            }

            var tradeRows = rawData.Where(r => r.Type == "Trade").ToList();
            var depthRows = rawData.Where(r => r.Type == "Depth").ToList();

            Console.WriteLine($"[INFO] Trade rows: {tradeRows.Count}");
            Console.WriteLine($"[INFO] Depth rows: {depthRows.Count}");

            if (tradeRows.Count == 0)
            {
                Console.WriteLine("[WARNING] No Trade data.");
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

            Console.WriteLine($"[INFO] Processed Trade rows: {result.Count}");
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
            if (closePath == null)
                closePath = @"C:\Users\User\Documents\_02_bt\Backtest_tick_module\close.parquet";

            if (!File.Exists(closePath))
            {
                Console.WriteLine($"[WARNING] Close price file not found: {closePath}");
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
                    Console.WriteLine($"[WARNING] No previous close price found for {date}");
                    return null;
                }

                var prevDate = prevDates.Last();
                if (closeData[prevDate].TryGetValue(stockId, out double refPrice))
                {
                    Console.WriteLine($"[INFO] Previous close: {refPrice:F2} (date: {prevDate:yyyy-MM-dd})");
                    return refPrice;
                }
                else
                {
                    Console.WriteLine($"[WARNING] Stock {stockId} not found in close.parquet");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to read close.parquet: {ex.Message}");
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
                        "company_basic_info.parquet",
                        @"D:\feature_data\company_basic_info.parquet",
                        @"C:\Users\User\Documents\_02_bt\Backtest_tick_module\company_basic_info.parquet"
                    };

                    foreach (var path in possiblePaths)
                    {
                        if (File.Exists(path))
                        {
                            _companyNameCache = ParquetHelper.ReadCompanyInfo(path);
                            Console.WriteLine($"[INFO] Loaded company info from: {path}");
                            break;
                        }
                    }

                    _companyNameCache ??= new Dictionary<string, string>();
                }

                return _companyNameCache.TryGetValue(stockId, out var name) ? name : stockId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Failed to load company name: {ex.Message}");
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
                Console.WriteLine("[ERROR] Data is empty");
                return false;
            }

            // Check time ordering
            for (int i = 1; i < data.Count; i++)
            {
                if (data[i].Time < data[i - 1].Time)
                {
                    Console.WriteLine("[WARNING] Data not sorted by time, sorting now.");
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

    /// <summary>
    /// Helper class for reading parquet files.
    /// In a real implementation, use Parquet.Net or Apache.Arrow NuGet packages.
    /// These methods provide the contract that actual parquet reading must fulfill.
    /// </summary>
    public static class ParquetHelper
    {
        /// <summary>
        /// Reads a feature parquet file and returns a list of TickData.
        /// Implementation requires Parquet.Net or Apache.Arrow.
        /// </summary>
        public static List<TickData> ReadParquetToTickData(string path)
        {
            // Placeholder: In production, use Parquet.Net / Apache.Arrow to read each column
            // and populate TickData objects. The parquet schema contains columns:
            // time, price, volume, tick_type, type, day_high, bid_ask_ratio,
            // bid1_volume..bid5_volume, ask1_volume..ask5_volume,
            // bid_volume_5level, ask_volume_5level, ratio_15s_300s,
            // pct_2min, pct_3min, pct_5min, low_1m, low_3m, low_5m, low_10m, low_15m
            Console.WriteLine($"[INFO] Reading parquet file: {path}");
            Console.WriteLine("[WARNING] ParquetHelper.ReadParquetToTickData requires Parquet.Net or Apache.Arrow implementation.");
            return new List<TickData>();
        }

        /// <summary>
        /// Reads close.parquet: returns Dictionary&lt;DateTime, Dictionary&lt;string, double&gt;&gt;
        /// where outer key is date, inner key is stock_id, value is close price.
        /// </summary>
        public static Dictionary<DateTime, Dictionary<string, double>> ReadCloseParquet(string path)
        {
            Console.WriteLine($"[INFO] Reading close parquet: {path}");
            Console.WriteLine("[WARNING] ParquetHelper.ReadCloseParquet requires Parquet.Net or Apache.Arrow implementation.");
            return new Dictionary<DateTime, Dictionary<string, double>>();
        }

        /// <summary>
        /// Reads company basic info parquet: returns Dictionary&lt;stockId, companyName&gt;.
        /// </summary>
        public static Dictionary<string, string> ReadCompanyInfo(string path)
        {
            Console.WriteLine($"[INFO] Reading company info: {path}");
            Console.WriteLine("[WARNING] ParquetHelper.ReadCompanyInfo requires Parquet.Net or Apache.Arrow implementation.");
            return new Dictionary<string, string>();
        }
    }
}
