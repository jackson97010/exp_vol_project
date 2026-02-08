using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using BacktestModule.Core;

namespace BacktestModule.Exporters
{
    /// <summary>
    /// Early-morning entry strategy exporter.
    /// Exports entry/exit records and charts to D:/1min_entry for early-morning breakout trades.
    /// Calculates PnL with commission (0.17% brokerage fee for Taiwan stocks).
    /// </summary>
    public class EarlyEntryExporter
    {
        private readonly string _basePath;
        private readonly Dictionary<string, object> _config;
        private readonly EarlyChartCreator _chartCreator;
        private readonly ILogger<EarlyEntryExporter> _logger;

        private string _currentDate;
        private string _datePath;
        private readonly List<Dictionary<string, object>> _tradesBuffer;

        /// <summary>
        /// Commission rate for Taiwan stock brokerage (0.17%).
        /// </summary>
        private const double CommissionRate = 0.0017;

        /// <summary>
        /// Default total shares per trade: 12 lots * 1000 shares.
        /// </summary>
        private const int TotalShares = 12 * 1000;

        /// <summary>
        /// Initializes the early entry exporter.
        /// </summary>
        /// <param name="basePath">Base output path.</param>
        /// <param name="config">Strategy configuration dictionary. Can be null.</param>
        /// <param name="logger">Logger instance. Can be null.</param>
        public EarlyEntryExporter(
            string basePath = "D:/1min_entry",
            Dictionary<string, object> config = null,
            ILogger<EarlyEntryExporter> logger = null)
        {
            _basePath = basePath;
            _config = config ?? new Dictionary<string, object>();
            _chartCreator = new EarlyChartCreator(config);
            _tradesBuffer = new List<Dictionary<string, object>>();
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EarlyEntryExporter>.Instance;
        }

        /// <summary>
        /// Sets the current date and creates the corresponding output folder.
        /// </summary>
        /// <param name="date">Date string (YYYY-MM-DD).</param>
        public void SetDate(string date)
        {
            _currentDate = date;
            _datePath = Path.Combine(_basePath, date);
            Directory.CreateDirectory(_datePath);
            _logger.LogInformation("Output path set: {DatePath}", _datePath);
        }

        /// <summary>
        /// Adds a trade record to the buffer.
        /// </summary>
        /// <param name="tradeRecord">Trade record dictionary.</param>
        public void AddTrade(Dictionary<string, object> tradeRecord)
        {
            _tradesBuffer.Add(tradeRecord);
        }

        /// <summary>
        /// Exports a single stock's trade record including CSV, JSON, and chart.
        /// </summary>
        /// <param name="stockId">Stock code.</param>
        /// <param name="tradeRecord">Trade record dictionary.</param>
        /// <param name="tickData">Tick data list for chart generation. Can be null.</param>
        /// <param name="massiveThreshold">Massive matching threshold in TWD. Can be null.</param>
        public void ExportSingleTrade(
            string stockId,
            Dictionary<string, object> tradeRecord,
            List<TickData> tickData = null,
            double? massiveThreshold = null)
        {
            if (string.IsNullOrEmpty(_currentDate))
            {
                _logger.LogError("Date not set, cannot export.");
                return;
            }

            // Create stock folder
            string stockPath = Path.Combine(_datePath, stockId);
            Directory.CreateDirectory(stockPath);

            // Prepare entry data
            var entryData = new Dictionary<string, object>
            {
                ["\u65E5\u671F"] = _currentDate,                                      // 日期
                ["\u80A1\u7968\u4EE3\u78BC"] = stockId,                               // 股票代碼
                ["\u9032\u5834\u6642\u9593"] = GetValue(tradeRecord, "entry_time"),    // 進場時間
                ["\u9032\u5834\u50F9\u683C"] = GetValue(tradeRecord, "entry_price"),   // 進場價格
                ["\u9032\u5834Ratio"] = GetValue(tradeRecord, "entry_ratio")           // 進場Ratio
            };

            // Process exit data (may have multiple batches)
            var exitData = new List<Dictionary<string, object>>();
            if (tradeRecord.ContainsKey("exits") && tradeRecord["exits"] is List<Dictionary<string, object>> exits)
            {
                for (int i = 0; i < exits.Count; i++)
                {
                    int num = i + 1;
                    var exitRecord = exits[i];
                    exitData.Add(new Dictionary<string, object>
                    {
                        [$"\u51FA\u5834{num}_\u6642\u9593"] = GetValue(exitRecord, "time"),    // 出場{n}_時間
                        [$"\u51FA\u5834{num}_\u50F9\u683C"] = GetValue(exitRecord, "price"),    // 出場{n}_價格
                        [$"\u51FA\u5834{num}_\u539F\u56E0"] = GetValue(exitRecord, "reason"),   // 出場{n}_原因
                        [$"\u51FA\u5834{num}_\u6BD4\u4F8B"] = GetValue(exitRecord, "ratio")    // 出場{n}_比例
                    });
                }
            }

            // Calculate profit/loss
            var profitData = CalculateProfit(tradeRecord);

            // Merge all data
            var completeData = new Dictionary<string, object>(entryData);
            foreach (var exitDict in exitData)
            {
                foreach (var kvp in exitDict)
                    completeData[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in profitData)
                completeData[kvp.Key] = kvp.Value;

            // Export CSV
            string csvPath = Path.Combine(stockPath, "entry_exit.csv");
            WriteSingleRecordCsv(completeData, csvPath);
            _logger.LogInformation("Trade record exported: {CsvPath}", csvPath);

            // Export JSON (detailed)
            string jsonPath = Path.Combine(stockPath, "trade_detail.json");
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(tradeRecord, jsonOptions), Encoding.UTF8);

            // Generate chart (if tick data available)
            if (tickData != null && tickData.Count > 0)
            {
                try
                {
                    string chartPath = Path.Combine(stockPath, "chart.html");
                    double threshold = massiveThreshold ?? 1_000_000.0;
                    _chartCreator.CreateChart(tickData, tradeRecord, chartPath, stockId, _currentDate, threshold);
                    _logger.LogInformation("Chart generated: {ChartPath}", chartPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chart generation failed.");
                }
            }
        }

        /// <summary>
        /// Calculates trade profit/loss using weighted average exit price.
        /// Applies commission: position_value * 0.0017 (Taiwan stock brokerage fee).
        /// </summary>
        /// <param name="tradeRecord">Trade record dictionary.</param>
        /// <returns>Profit data dictionary with average exit price, PnL, return rate, and commission.</returns>
        public Dictionary<string, object> CalculateProfit(Dictionary<string, object> tradeRecord)
        {
            double entryPrice = tradeRecord.ContainsKey("entry_price")
                ? Convert.ToDouble(tradeRecord["entry_price"]) : 0;
            string stockId = tradeRecord.ContainsKey("stock_id")
                ? tradeRecord["stock_id"]?.ToString() : "Unknown";

            double avgExitPrice;

            // Calculate weighted average exit price
            if (tradeRecord.ContainsKey("exits") && tradeRecord["exits"] is List<Dictionary<string, object>> exits && exits.Count > 0)
            {
                double weightedExitSum = 0;
                double totalExitRatio = 0;

                foreach (var exitRecord in exits)
                {
                    double ratio = exitRecord.ContainsKey("ratio") ? Convert.ToDouble(exitRecord["ratio"]) : 0;
                    double price = exitRecord.ContainsKey("price") ? Convert.ToDouble(exitRecord["price"]) : 0;
                    weightedExitSum += price * ratio;
                    totalExitRatio += ratio;
                }

                // Check if total ratio is anomalous (allow 0.1% floating-point tolerance)
                if (totalExitRatio > 1.001)
                {
                    _logger.LogWarning("Warning: {StockId} total exit ratio {Ratio:F4} exceeds 100%, possible double counting!",
                        stockId, totalExitRatio);
                }
                else if (totalExitRatio < 0.999)
                {
                    _logger.LogWarning("Warning: {StockId} total exit ratio {Ratio:F4} is less than 100%, may have unrecorded exit batches!",
                        stockId, totalExitRatio);
                }

                // Calculate weighted average exit price
                if (totalExitRatio > 0)
                {
                    avgExitPrice = weightedExitSum / totalExitRatio;
                }
                else
                {
                    // If no ratio information, use simple average
                    var exitPrices = exits
                        .Where(e => e.ContainsKey("price"))
                        .Select(e => Convert.ToDouble(e["price"]))
                        .ToList();
                    avgExitPrice = exitPrices.Count > 0 ? exitPrices.Average() : 0;
                    _logger.LogWarning("Warning: {StockId} has no exit ratio information, using simple average.", stockId);
                }
            }
            else
            {
                avgExitPrice = 0;
                _logger.LogWarning("Warning: {StockId} has no exit records.", stockId);
            }

            // Calculate PnL per specification
            double positionValue = entryPrice * TotalShares;
            double exitValue = avgExitPrice * TotalShares;

            // Commission: position_value * 0.0017
            double commission = positionValue * CommissionRate;

            // Net PnL = exit value - position value - commission
            double profit = exitValue - positionValue - commission;

            // Return rate = net PnL / position value * 100%
            double returnRate = positionValue > 0 ? (profit / positionValue) * 100 : 0;

            return new Dictionary<string, object>
            {
                ["\u5E73\u5747\u51FA\u5834\u50F9\u683C"] = Math.Round(avgExitPrice, 2),   // 平均出場價格
                ["\u640D\u76CA\u91D1\u984D"] = Math.Round(profit, 2),                      // 損益金額
                ["\u5831\u916C\u7387%"] = Math.Round(returnRate, 2),                       // 報酬率%
                ["\u624B\u7E8C\u8CBB"] = Math.Round(commission, 2)                         // 手續費
            };
        }

        /// <summary>
        /// Exports a daily summary of all buffered trades.
        /// </summary>
        public void ExportSummary()
        {
            if (string.IsNullOrEmpty(_currentDate) || _tradesBuffer.Count == 0)
            {
                _logger.LogInformation("No trade data to export.");
                return;
            }

            // Prepare summary data
            var summaryData = new List<Dictionary<string, object>>();
            foreach (var trade in _tradesBuffer)
            {
                var profitData = CalculateProfit(trade);
                summaryData.Add(new Dictionary<string, object>
                {
                    ["\u80A1\u7968\u4EE3\u78BC"] = GetValue(trade, "stock_id"),             // 股票代碼
                    ["\u9032\u5834\u6642\u9593"] = GetValue(trade, "entry_time"),            // 進場時間
                    ["\u9032\u5834\u50F9\u683C"] = GetValue(trade, "entry_price"),           // 進場價格
                    ["\u5E73\u5747\u51FA\u5834\u50F9\u683C"] = profitData["\u5E73\u5747\u51FA\u5834\u50F9\u683C"],
                    ["\u640D\u76CA\u91D1\u984D"] = profitData["\u640D\u76CA\u91D1\u984D"],
                    ["\u5831\u916C\u7387%"] = profitData["\u5831\u916C\u7387%"]
                });
            }

            // Add totals row
            if (summaryData.Count > 0)
            {
                double totalPnl = summaryData.Sum(d =>
                    d.ContainsKey("\u640D\u76CA\u91D1\u984D") ? Convert.ToDouble(d["\u640D\u76CA\u91D1\u984D"]) : 0);
                double avgReturn = summaryData.Average(d =>
                    d.ContainsKey("\u5831\u916C\u7387%") ? Convert.ToDouble(d["\u5831\u916C\u7387%"]) : 0);

                summaryData.Add(new Dictionary<string, object>
                {
                    ["\u80A1\u7968\u4EE3\u78BC"] = "\u7E3D\u8A08",                          // 總計
                    ["\u9032\u5834\u6642\u9593"] = "",
                    ["\u9032\u5834\u50F9\u683C"] = "",
                    ["\u5E73\u5747\u51FA\u5834\u50F9\u683C"] = "",
                    ["\u640D\u76CA\u91D1\u984D"] = totalPnl,
                    ["\u5831\u916C\u7387%"] = avgReturn
                });
            }

            // Write summary CSV
            string summaryPath = Path.Combine(_datePath, "summary.csv");
            WriteDictionaryListToCsv(summaryData, summaryPath);
            _logger.LogInformation("Trade summary exported: {SummaryPath}", summaryPath);

            // Write detailed JSON
            string detailPath = Path.Combine(_datePath, "trades_detail.json");
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(detailPath, JsonSerializer.Serialize(_tradesBuffer, jsonOptions), Encoding.UTF8);

            _logger.LogInformation("Exported {Count} trade records.", _tradesBuffer.Count);
        }

        /// <summary>
        /// Exports a performance report with aggregated statistics.
        /// </summary>
        public void ExportPerformanceReport()
        {
            if (_tradesBuffer.Count == 0)
                return;

            // Calculate performance metrics
            int totalTrades = _tradesBuffer.Count;
            int profitableTrades = _tradesBuffer.Count(t => CalculateProfit(t)
                .ContainsKey("\u640D\u76CA\u91D1\u984D") &&
                Convert.ToDouble(CalculateProfit(t)["\u640D\u76CA\u91D1\u984D"]) > 0);

            double winRate = totalTrades > 0 ? (double)profitableTrades / totalTrades * 100 : 0;

            var allProfits = _tradesBuffer.Select(t =>
                Convert.ToDouble(CalculateProfit(t)["\u640D\u76CA\u91D1\u984D"])).ToList();
            double totalProfit = allProfits.Sum();
            double avgProfit = allProfits.Count > 0 ? allProfits.Average() : 0;

            var allReturns = _tradesBuffer.Select(t =>
                Convert.ToDouble(CalculateProfit(t)["\u5831\u916C\u7387%"])).ToList();
            double avgReturn = allReturns.Count > 0 ? allReturns.Average() : 0;

            var performance = new Dictionary<string, object>
            {
                ["\u4EA4\u6613\u65E5\u671F"] = _currentDate,                            // 交易日期
                ["\u7E3D\u4EA4\u6613\u6B21\u6578"] = totalTrades,                       // 總交易次數
                ["\u7372\u5229\u6B21\u6578"] = profitableTrades,                         // 獲利次數
                ["\u52DD\u7387%"] = Math.Round(winRate, 2),                              // 勝率%
                ["\u7E3D\u640D\u76CA"] = Math.Round(totalProfit, 2),                     // 總損益
                ["\u5E73\u5747\u640D\u76CA"] = Math.Round(avgProfit, 2),                 // 平均損益
                ["\u5E73\u5747\u5831\u916C\u7387%"] = Math.Round(avgReturn, 2),          // 平均報酬率%
                ["\u7B56\u7565\u6A21\u5F0F"] = "\u65E9\u76E4\u7A81\u7834(09:01-09:05)" // 策略模式: 早盤突破(09:01-09:05)
            };

            // Write performance report
            string reportPath = Path.Combine(_basePath, "reports");
            Directory.CreateDirectory(reportPath);

            string reportFile = Path.Combine(reportPath, $"performance_{_currentDate}.json");
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(reportFile, JsonSerializer.Serialize(performance, jsonOptions), Encoding.UTF8);

            _logger.LogInformation("Performance report exported: {ReportFile}", reportFile);
            _logger.LogInformation("Win rate: {WinRate:F2}%, Average return: {AvgReturn:F2}%", winRate, avgReturn);
        }

        /// <summary>
        /// Safely gets a value from a dictionary with a default of empty string.
        /// </summary>
        private static object GetValue(Dictionary<string, object> dict, string key)
        {
            return dict.ContainsKey(key) ? dict[key] : "";
        }

        /// <summary>
        /// Writes a single record as a one-row CSV file.
        /// </summary>
        private void WriteSingleRecordCsv(Dictionary<string, object> record, string filePath)
        {
            using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));
            var keys = record.Keys.ToList();
            writer.WriteLine(string.Join(",", keys.Select(EscapeCsvField)));
            var values = keys.Select(k =>
            {
                var val = record[k];
                return val != null ? EscapeCsvField(FormatValue(val)) : "";
            });
            writer.WriteLine(string.Join(",", values));
        }

        /// <summary>
        /// Writes a list of dictionaries to CSV.
        /// </summary>
        private void WriteDictionaryListToCsv(List<Dictionary<string, object>> records, string filePath)
        {
            if (records.Count == 0) return;

            var allKeys = new List<string>();
            var keySet = new HashSet<string>();
            foreach (var record in records)
            {
                foreach (var key in record.Keys)
                {
                    if (keySet.Add(key)) allKeys.Add(key);
                }
            }

            using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));
            writer.WriteLine(string.Join(",", allKeys.Select(EscapeCsvField)));

            foreach (var record in records)
            {
                var values = allKeys.Select(key =>
                {
                    if (record.TryGetValue(key, out object value) && value != null)
                        return EscapeCsvField(FormatValue(value));
                    return "";
                });
                writer.WriteLine(string.Join(",", values));
            }
        }

        private static string FormatValue(object value)
        {
            if (value == null) return "";
            if (value is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            if (value is double d) return d.ToString(CultureInfo.InvariantCulture);
            if (value is float f) return f.ToString(CultureInfo.InvariantCulture);
            if (value is decimal dec) return dec.ToString(CultureInfo.InvariantCulture);
            return value.ToString();
        }

        private static string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }
    }
}
