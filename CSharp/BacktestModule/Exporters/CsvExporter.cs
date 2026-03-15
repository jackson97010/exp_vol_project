using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using BacktestModule.Core;
using BacktestModule.Strategy;

namespace BacktestModule.Exporters
{
    /// <summary>
    /// CSV exporter for backtest results.
    /// Exports summary statistics and detailed trade records to CSV files.
    /// </summary>
    public class CsvExporter : ICsvExporter
    {
        private readonly string _outputBaseDir;
        private readonly ILogger<CsvExporter> _logger;

        /// <summary>
        /// Default output base directory.
        /// </summary>
        public const string DefaultOutputBaseDir = @"D:\backtest_results";

        /// <summary>
        /// Initializes the CSV exporter.
        /// </summary>
        /// <param name="outputBaseDir">Output base directory path.</param>
        /// <param name="logger">Logger instance. Can be null.</param>
        public CsvExporter(
            string outputBaseDir = DefaultOutputBaseDir,
            ILogger<CsvExporter> logger = null)
        {
            _outputBaseDir = outputBaseDir;
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CsvExporter>.Instance;
        }

        /// <summary>
        /// Exports summary statistics to a CSV file.
        /// File path: {baseDir}/{date}/backtest_summary_{date}.csv
        /// </summary>
        /// <param name="results">List of per-stock statistics dictionaries.</param>
        /// <param name="date">Date string.</param>
        public void ExportSummaryToCsv(List<Dictionary<string, object>> results, string date)
        {
            if (results == null || results.Count == 0)
            {
                _logger.LogWarning("No trade results to export.");
                return;
            }

            // Create output directory
            string outputDir = Path.Combine(_outputBaseDir, date);
            Directory.CreateDirectory(outputDir);

            // Output file path
            string csvPath = Path.Combine(outputDir, $"backtest_summary_{date}.csv");

            // Write CSV with UTF-8 BOM for Excel compatibility
            WriteDictionaryListToCsv(results, csvPath);

            _logger.LogInformation("Summary statistics exported to: {CsvPath}", csvPath);

            // Show totals
            double totalPnl = results.Sum(r =>
                r.ContainsKey("\u640D\u76CA") ? Convert.ToDouble(r["\u640D\u76CA"]) : 0);
            int totalTrades = results.Sum(r =>
                r.ContainsKey("\u9032\u5834\u6B21\u6578") ? Convert.ToInt32(r["\u9032\u5834\u6B21\u6578"]) : 0);
            int totalWins = results.Count(r =>
                r.ContainsKey("\u640D\u76CA") && Convert.ToDouble(r["\u640D\u76CA"]) > 0);

            _logger.LogInformation("");
            _logger.LogInformation(new string('=', 60));
            _logger.LogInformation("Summary:");
            _logger.LogInformation("  Total trades: {TotalTrades}", totalTrades);
            _logger.LogInformation("  Winning stocks: {TotalWins}", totalWins);
            if (totalTrades > 0)
            {
                _logger.LogInformation("  Win rate: {WinRate:F2}%",
                    (double)totalWins / totalTrades * 100);
            }
            _logger.LogInformation("  Total PnL: {TotalPnl:N0}", totalPnl);
            _logger.LogInformation(new string('=', 60));
        }

        /// <summary>
        /// Exports detailed trade records (all stocks) to a summary CSV.
        /// Also generates a batch script CSV.
        /// </summary>
        /// <param name="allTradeDetails">All trade detail dictionaries across all stocks.</param>
        /// <param name="date">Date string.</param>
        public void ExportDetailedTradesToCsv(List<Dictionary<string, object>> allTradeDetails, string date)
        {
            if (allTradeDetails == null || allTradeDetails.Count == 0)
            {
                _logger.LogWarning("No trade records to export.");
                return;
            }

            // Create output directory
            string outputDir = Path.Combine(_outputBaseDir, date);
            Directory.CreateDirectory(outputDir);

            // Output file path (uses the same summary file name)
            string csvPath = Path.Combine(outputDir, $"backtest_summary_{date}.csv");

            // Sort by stock code, entry time, and exit batch
            var sorted = allTradeDetails
                .OrderBy(d => d.ContainsKey("\u80A1\u7968\u4EE3\u78BC") ? d["\u80A1\u7968\u4EE3\u78BC"]?.ToString() : "")
                .ThenBy(d => d.ContainsKey("\u9032\u5834\u6642\u9593") ? d["\u9032\u5834\u6642\u9593"]?.ToString() : "")
                .ThenBy(d => d.ContainsKey("\u51FA\u5834\u6279\u6B21") ? d["\u51FA\u5834\u6279\u6B21"]?.ToString() : "")
                .ToList();

            // Write CSV
            WriteDictionaryListToCsv(sorted, csvPath);

            _logger.LogInformation("Detailed trade records exported to: {CsvPath}", csvPath);

            // Show totals
            var grouped = sorted
                .Where(d => d.ContainsKey("\u80A1\u7968\u4EE3\u78BC") && d.ContainsKey("\u4EA4\u6613\u7DE8\u865F"))
                .GroupBy(d => new
                {
                    StockId = d["\u80A1\u7968\u4EE3\u78BC"]?.ToString(),
                    TradeNum = d["\u4EA4\u6613\u7DE8\u865F"]?.ToString()
                })
                .ToList();

            int totalTrades = grouped.Count;
            double totalPnl = grouped.Sum(g =>
            {
                var first = g.First();
                return first.ContainsKey("\u7E3D\u640D\u76CA\u91D1\u984D")
                    ? Convert.ToDouble(first["\u7E3D\u640D\u76CA\u91D1\u984D"])
                    : 0;
            });
            int totalWins = grouped.Count(g =>
            {
                var first = g.First();
                return first.ContainsKey("\u7E3D\u640D\u76CA\u91D1\u984D")
                    && Convert.ToDouble(first["\u7E3D\u640D\u76CA\u91D1\u984D"]) > 0;
            });

            int stockCount = sorted
                .Where(d => d.ContainsKey("\u80A1\u7968\u4EE3\u78BC"))
                .Select(d => d["\u80A1\u7968\u4EE3\u78BC"]?.ToString())
                .Distinct()
                .Count();

            _logger.LogInformation("");
            _logger.LogInformation(new string('=', 60));
            _logger.LogInformation("Summary:");
            _logger.LogInformation("  Stock count: {StockCount}", stockCount);
            _logger.LogInformation("  Total trades: {TotalTrades}", totalTrades);
            _logger.LogInformation("  Winning trades: {TotalWins}", totalWins);
            if (totalTrades > 0)
            {
                _logger.LogInformation("  Win rate: {WinRate:F2}%",
                    (double)totalWins / totalTrades * 100);
            }
            _logger.LogInformation("  Total PnL: {TotalPnl:N0}", totalPnl);
            _logger.LogInformation(new string('=', 60));

            // Also generate batch script CSV
            ExportBatchScriptCsv(allTradeDetails, date);
        }

        /// <summary>
        /// Generates the batch script CSV file for trade_pnl_analyzer.py compatibility.
        /// Format: trade_id, entry_time, stock_id, entry_price, exit_price, exit_ratio, exit_type
        /// trade_id does not include the exit batch number, so the analyzer can group
        /// multiple exit batches of the same trade to calculate weighted average exit price.
        /// </summary>
        /// <param name="allTradeDetails">All trade detail dictionaries.</param>
        /// <param name="date">Date string.</param>
        private void ExportBatchScriptCsv(List<Dictionary<string, object>> allTradeDetails, string date)
        {
            if (allTradeDetails == null || allTradeDetails.Count == 0)
                return;

            // Convert to batch script format
            var batchRecords = new List<Dictionary<string, object>>();

            foreach (var detail in allTradeDetails)
            {
                string stockCode = detail.ContainsKey("\u80A1\u7968\u4EE3\u78BC")
                    ? detail["\u80A1\u7968\u4EE3\u78BC"]?.ToString() : "";
                object tradeNum = detail.ContainsKey("\u4EA4\u6613\u7DE8\u865F")
                    ? detail["\u4EA4\u6613\u7DE8\u865F"] : 0;

                var record = new Dictionary<string, object>
                {
                    ["trade_id"] = $"{stockCode}_T{tradeNum}",  // Does not include _B{batch}
                    ["entry_time"] = detail.ContainsKey("\u9032\u5834\u6642\u9593") ? detail["\u9032\u5834\u6642\u9593"] : "",
                    ["stock_id"] = stockCode,
                    ["entry_price"] = detail.ContainsKey("\u9032\u5834\u50F9\u683C") ? detail["\u9032\u5834\u50F9\u683C"] : 0,
                    ["exit_price"] = detail.ContainsKey("\u51FA\u5834\u50F9\u683C") ? detail["\u51FA\u5834\u50F9\u683C"] : 0,
                    ["exit_ratio"] = detail.ContainsKey("\u51FA\u5834\u6BD4\u4F8B") ? detail["\u51FA\u5834\u6BD4\u4F8B"] : 0,
                    ["exit_type"] = detail.ContainsKey("\u51FA\u5834\u539F\u56E0") ? detail["\u51FA\u5834\u539F\u56E0"] : ""
                };
                batchRecords.Add(record);
            }

            // Output to project root directory
            string batchCsvPath = $"backtest_results_{date}.csv";
            WriteDictionaryListToCsv(batchRecords, batchCsvPath);

            _logger.LogInformation("Batch script CSV exported to: {CsvPath}", batchCsvPath);
        }

        /// <summary>
        /// Writes a list of dictionaries to a CSV file with UTF-8 BOM encoding.
        /// All keys from all dictionaries are collected as headers. Missing keys produce empty cells.
        /// </summary>
        /// <param name="records">List of dictionaries to write.</param>
        /// <param name="filePath">Output file path.</param>
        private void WriteDictionaryListToCsv(List<Dictionary<string, object>> records, string filePath)
        {
            if (records.Count == 0)
                return;

            // Collect all unique keys in order of first appearance
            var allKeys = new List<string>();
            var keySet = new HashSet<string>();
            foreach (var record in records)
            {
                foreach (var key in record.Keys)
                {
                    if (keySet.Add(key))
                    {
                        allKeys.Add(key);
                    }
                }
            }

            // Write with UTF-8 BOM for Excel to correctly display Chinese characters
            using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));

            // Write header
            writer.WriteLine(string.Join(",", allKeys.Select(EscapeCsvField)));

            // Write data rows
            foreach (var record in records)
            {
                var values = allKeys.Select(key =>
                {
                    if (record.TryGetValue(key, out object value) && value != null)
                    {
                        return EscapeCsvField(FormatValue(value));
                    }
                    return "";
                });
                writer.WriteLine(string.Join(",", values));
            }
        }

        /// <summary>
        /// Formats a value for CSV output. Handles DateTime, double, and other types.
        /// </summary>
        private static string FormatValue(object value)
        {
            if (value == null)
                return "";

            if (value is DateTime dt)
                return dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

            if (value is double d)
                return d.ToString(CultureInfo.InvariantCulture);

            if (value is float f)
                return f.ToString(CultureInfo.InvariantCulture);

            if (value is decimal dec)
                return dec.ToString(CultureInfo.InvariantCulture);

            return value.ToString();
        }

        /// <summary>
        /// Escapes a CSV field value by quoting it if it contains commas, quotes, or newlines.
        /// </summary>
        private static string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field))
                return "";

            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            {
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            }
            return field;
        }
    }
}
