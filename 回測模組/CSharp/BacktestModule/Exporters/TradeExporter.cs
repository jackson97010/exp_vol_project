using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using BacktestModule.Analytics;
using BacktestModule.Core;

namespace BacktestModule.Exporters
{
    /// <summary>
    /// Trade exporter for per-stock detailed trade records.
    /// Outputs CSV files where each exit batch is a separate row.
    /// </summary>
    public class TradeExporter
    {
        private readonly string _outputBaseDir;
        private readonly TradeDetailsProcessor _detailsProcessor;
        private readonly ILogger<TradeExporter> _logger;

        /// <summary>
        /// Default output base directory.
        /// </summary>
        public const string DefaultOutputBaseDir = @"D:\backtest_results";

        /// <summary>
        /// Initializes the trade exporter.
        /// </summary>
        /// <param name="outputBaseDir">Output base directory path.</param>
        /// <param name="logger">Logger instance. Can be null.</param>
        public TradeExporter(
            string outputBaseDir = DefaultOutputBaseDir,
            ILogger<TradeExporter> logger = null)
        {
            _outputBaseDir = outputBaseDir;
            _detailsProcessor = new TradeDetailsProcessor();
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TradeExporter>.Instance;
        }

        /// <summary>
        /// Exports detailed entry/exit records to CSV for a single stock.
        /// Each exit batch gets its own row. Also handles trailing stop, two-stage, and no-exit scenarios.
        /// File path: {baseDir}/{date}/{stockId}_trade_details_{date}.csv
        /// </summary>
        /// <param name="trades">List of trade records for the stock.</param>
        /// <param name="stockId">Stock code.</param>
        /// <param name="date">Date string.</param>
        public void ExportTradeDetailsToCsv(List<TradeRecord> trades, string stockId, string date)
        {
            if (trades == null || trades.Count == 0)
            {
                _logger.LogWarning("Stock {StockId} has no trade records to export.", stockId);
                return;
            }

            // Create output directory
            string outputDir = Path.Combine(_outputBaseDir, date);
            Directory.CreateDirectory(outputDir);

            // Output file path
            string csvPath = Path.Combine(outputDir, $"{stockId}_trade_details_{date}.csv");

            // Prepare detailed trade data (each exit batch is a separate row)
            var tradeDetails = new List<Dictionary<string, object>>();

            for (int i = 0; i < trades.Count; i++)
            {
                var trade = trades[i];
                int tradeNum = i + 1;

                // Calculate actual exit price (weighted average)
                double exitPrice = _detailsProcessor.CalculateActualExitPrice(trade);
                double pnl = (exitPrice - trade.EntryPrice) * 1000; // 1 lot
                double pnlPercent = ((exitPrice - trade.EntryPrice) / trade.EntryPrice) * 100;

                // Handle trailing stop exits (may have multiple batches)
                if (trade.TrailingExitDetails != null && trade.TrailingExitDetails.Count > 0)
                {
                    AddTrailingExitRows(tradeDetails, trade, tradeNum, stockId, exitPrice, pnl, pnlPercent);
                }
                // Handle two-stage exit mode
                else if (trade.PartialExitTime.HasValue || trade.FinalExitTime.HasValue)
                {
                    AddTwoStageExitRows(tradeDetails, trade, tradeNum, stockId, exitPrice, pnl, pnlPercent);
                }
                // No exit record (should not happen in theory)
                else
                {
                    AddNoExitRow(tradeDetails, trade, tradeNum, stockId);
                }
            }

            // Write CSV
            if (tradeDetails.Count > 0)
            {
                WriteDictionaryListToCsv(tradeDetails, csvPath);
                _logger.LogInformation("Detailed trade records exported to: {CsvPath}", csvPath);
                _logger.LogInformation("Total {Count} exit records exported.", tradeDetails.Count);
            }
        }

        /// <summary>
        /// Adds trailing stop exit rows.
        /// </summary>
        private void AddTrailingExitRows(
            List<Dictionary<string, object>> tradeDetails,
            TradeRecord trade, int tradeNum, string stockId,
            double exitPrice, double pnl, double pnlPercent)
        {
            double outsideVol3s = trade.EntryOutsideVolume3s > 0
                ? trade.EntryOutsideVolume3s / 1_000_000.0 : 0.0;

            for (int j = 0; j < trade.TrailingExitDetails.Count; j++)
            {
                var exitDetail = trade.TrailingExitDetails[j];
                string level = exitDetail.ContainsKey("level") ? exitDetail["level"]?.ToString() : "";
                double price = exitDetail.ContainsKey("price") ? Convert.ToDouble(exitDetail["price"]) : 0.0;
                double ratio = exitDetail.ContainsKey("ratio") ? Convert.ToDouble(exitDetail["ratio"]) : 0.0;
                object time = exitDetail.ContainsKey("time") ? exitDetail["time"] : null;

                tradeDetails.Add(new Dictionary<string, object>
                {
                    ["\u4EA4\u6613\u7DE8\u865F"] = tradeNum,                         // 交易編號
                    ["\u80A1\u7968\u4EE3\u78BC"] = stockId,                           // 股票代碼
                    ["\u9032\u5834\u6642\u9593"] = trade.EntryTime,                   // 進場時間
                    ["\u9032\u5834\u50F9\u683C"] = trade.EntryPrice,                  // 進場價格
                    ["\u9032\u5834Ratio"] = trade.EntryRatio,                         // 進場Ratio
                    ["\u9032\u5834\u5916\u76E43\u79D2(M)"] = outsideVol3s,            // 進場外盤3秒(M)
                    ["\u9032\u5834\u6642DayHigh"] = trade.DayHighAtEntry,             // 進場時DayHigh
                    ["\u51FA\u5834\u6279\u6B21"] = j + 1,                             // 出場批次
                    ["\u51FA\u5834\u985E\u578B"] = $"\u79FB\u52D5\u505C\u5229_{level}", // 出場類型: 移動停利_{level}
                    ["\u51FA\u5834\u6642\u9593"] = time,                              // 出場時間
                    ["\u51FA\u5834\u50F9\u683C"] = price,                             // 出場價格
                    ["\u51FA\u5834\u6BD4\u4F8B"] = ratio * 100,                       // 出場比例
                    ["\u51FA\u5834\u539F\u56E0"] = $"\u8DCC\u7834{level}\u4F4E\u9EDE",// 出場原因: 跌破{level}低點
                    ["\u5BE6\u969B\u51FA\u5834\u5747\u50F9"] = exitPrice,             // 實際出場均價
                    ["\u7E3D\u640D\u76CA\u91D1\u984D"] = pnl,                        // 總損益金額
                    ["\u7E3D\u640D\u76CA\u767E\u5206\u6BD4"] = pnlPercent            // 總損益百分比
                });
            }

            // If there is entry price protection or market close exit (remaining position)
            if (trade.FinalExitTime.HasValue && !string.IsNullOrEmpty(trade.FinalExitReason))
            {
                double remainingRatio = 1.0 - trade.TrailingExitDetails.Sum(
                    e => e.ContainsKey("ratio") ? Convert.ToDouble(e["ratio"]) : 0.0);

                if (remainingRatio > 0)
                {
                    tradeDetails.Add(new Dictionary<string, object>
                    {
                        ["\u4EA4\u6613\u7DE8\u865F"] = tradeNum,
                        ["\u80A1\u7968\u4EE3\u78BC"] = stockId,
                        ["\u9032\u5834\u6642\u9593"] = trade.EntryTime,
                        ["\u9032\u5834\u50F9\u683C"] = trade.EntryPrice,
                        ["\u9032\u5834Ratio"] = trade.EntryRatio,
                        ["\u9032\u5834\u5916\u76E43\u79D2(M)"] = outsideVol3s,
                        ["\u9032\u5834\u6642DayHigh"] = trade.DayHighAtEntry,
                        ["\u51FA\u5834\u6279\u6B21"] = trade.TrailingExitDetails.Count + 1,
                        ["\u51FA\u5834\u985E\u578B"] = "\u6700\u7D42\u6E05\u5009",   // 最終清倉
                        ["\u51FA\u5834\u6642\u9593"] = trade.FinalExitTime,
                        ["\u51FA\u5834\u50F9\u683C"] = trade.FinalExitPrice,
                        ["\u51FA\u5834\u6BD4\u4F8B"] = remainingRatio * 100,
                        ["\u51FA\u5834\u539F\u56E0"] = trade.FinalExitReason,
                        ["\u5BE6\u969B\u51FA\u5834\u5747\u50F9"] = exitPrice,
                        ["\u7E3D\u640D\u76CA\u91D1\u984D"] = pnl,
                        ["\u7E3D\u640D\u76CA\u767E\u5206\u6BD4"] = pnlPercent
                    });
                }
            }
        }

        /// <summary>
        /// Adds two-stage exit rows (partial + optional reentry + final).
        /// </summary>
        private void AddTwoStageExitRows(
            List<Dictionary<string, object>> tradeDetails,
            TradeRecord trade, int tradeNum, string stockId,
            double exitPrice, double pnl, double pnlPercent)
        {
            int batch = 1;
            double outsideVol3s = trade.EntryOutsideVolume3s > 0
                ? trade.EntryOutsideVolume3s / 1_000_000.0 : 0.0;

            // Stage 1: Partial exit (reduce 50%)
            if (trade.PartialExitTime.HasValue)
            {
                tradeDetails.Add(new Dictionary<string, object>
                {
                    ["\u4EA4\u6613\u7DE8\u865F"] = tradeNum,
                    ["\u80A1\u7968\u4EE3\u78BC"] = stockId,
                    ["\u9032\u5834\u6642\u9593"] = trade.EntryTime,
                    ["\u9032\u5834\u50F9\u683C"] = trade.EntryPrice,
                    ["\u9032\u5834Ratio"] = trade.EntryRatio,
                    ["\u9032\u5834\u5916\u76E43\u79D2(M)"] = outsideVol3s,
                    ["\u9032\u5834\u6642DayHigh"] = trade.DayHighAtEntry,
                    ["\u51FA\u5834\u6279\u6B21"] = batch,
                    ["\u51FA\u5834\u985E\u578B"] = "\u6E1B\u78BC50%",                // 減碼50%
                    ["\u51FA\u5834\u6642\u9593"] = trade.PartialExitTime,
                    ["\u51FA\u5834\u50F9\u683C"] = trade.PartialExitPrice,
                    ["\u51FA\u5834\u6BD4\u4F8B"] = 50.0,
                    ["\u51FA\u5834\u539F\u56E0"] = trade.PartialExitReason ?? "",
                    ["\u5BE6\u969B\u51FA\u5834\u5747\u50F9"] = exitPrice,
                    ["\u7E3D\u640D\u76CA\u91D1\u984D"] = pnl,
                    ["\u7E3D\u640D\u76CA\u767E\u5206\u6BD4"] = pnlPercent
                });
                batch++;
            }

            // Reentry (if any)
            if (trade.ReentryTime.HasValue)
            {
                tradeDetails.Add(new Dictionary<string, object>
                {
                    ["\u4EA4\u6613\u7DE8\u865F"] = tradeNum,
                    ["\u80A1\u7968\u4EE3\u78BC"] = stockId,
                    ["\u9032\u5834\u6642\u9593"] = trade.EntryTime,
                    ["\u9032\u5834\u50F9\u683C"] = trade.EntryPrice,
                    ["\u9032\u5834Ratio"] = trade.EntryRatio,
                    ["\u9032\u5834\u5916\u76E43\u79D2(M)"] = outsideVol3s,
                    ["\u9032\u5834\u6642DayHigh"] = trade.DayHighAtEntry,
                    ["\u51FA\u5834\u6279\u6B21"] = (object)"\u56DE\u88DC",            // 回補
                    ["\u51FA\u5834\u985E\u578B"] = "\u56DE\u88DC\u9032\u5834",        // 回補進場
                    ["\u51FA\u5834\u6642\u9593"] = trade.ReentryTime,
                    ["\u51FA\u5834\u50F9\u683C"] = trade.ReentryPrice,
                    ["\u51FA\u5834\u6BD4\u4F8B"] = (object)"-",
                    ["\u51FA\u5834\u539F\u56E0"] = "\u50F9\u683C\u5275\u65B0\u9AD8\u4E14\u5916\u76E4\u589E\u52A0", // 價格創新高且外盤增加
                    ["\u5BE6\u969B\u51FA\u5834\u5747\u50F9"] = exitPrice,
                    ["\u7E3D\u640D\u76CA\u91D1\u984D"] = pnl,
                    ["\u7E3D\u640D\u76CA\u767E\u5206\u6BD4"] = pnlPercent
                });
            }

            // Final exit (close all)
            if (trade.FinalExitTime.HasValue)
            {
                string exitType = trade.ReentryTime.HasValue
                    ? "\u56DE\u88DC\u5F8C\u6E05\u5009"    // 回補後清倉
                    : "\u6E05\u5009";                       // 清倉
                double exitRatio = trade.PartialExitTime.HasValue ? 50.0 : 100.0;

                tradeDetails.Add(new Dictionary<string, object>
                {
                    ["\u4EA4\u6613\u7DE8\u865F"] = tradeNum,
                    ["\u80A1\u7968\u4EE3\u78BC"] = stockId,
                    ["\u9032\u5834\u6642\u9593"] = trade.EntryTime,
                    ["\u9032\u5834\u50F9\u683C"] = trade.EntryPrice,
                    ["\u9032\u5834Ratio"] = trade.EntryRatio,
                    ["\u9032\u5834\u5916\u76E43\u79D2(M)"] = outsideVol3s,
                    ["\u9032\u5834\u6642DayHigh"] = trade.DayHighAtEntry,
                    ["\u51FA\u5834\u6279\u6B21"] = batch,
                    ["\u51FA\u5834\u985E\u578B"] = exitType,
                    ["\u51FA\u5834\u6642\u9593"] = trade.FinalExitTime,
                    ["\u51FA\u5834\u50F9\u683C"] = trade.FinalExitPrice,
                    ["\u51FA\u5834\u6BD4\u4F8B"] = exitRatio,
                    ["\u51FA\u5834\u539F\u56E0"] = trade.FinalExitReason ?? "",
                    ["\u5BE6\u969B\u51FA\u5834\u5747\u50F9"] = exitPrice,
                    ["\u7E3D\u640D\u76CA\u91D1\u984D"] = pnl,
                    ["\u7E3D\u640D\u76CA\u767E\u5206\u6BD4"] = pnlPercent
                });
            }
        }

        /// <summary>
        /// Adds a no-exit row (should not happen in normal operation).
        /// </summary>
        private void AddNoExitRow(
            List<Dictionary<string, object>> tradeDetails,
            TradeRecord trade, int tradeNum, string stockId)
        {
            double outsideVol3s = trade.EntryOutsideVolume3s > 0
                ? trade.EntryOutsideVolume3s / 1_000_000.0 : 0.0;

            tradeDetails.Add(new Dictionary<string, object>
            {
                ["\u4EA4\u6613\u7DE8\u865F"] = tradeNum,
                ["\u80A1\u7968\u4EE3\u78BC"] = stockId,
                ["\u9032\u5834\u6642\u9593"] = trade.EntryTime,
                ["\u9032\u5834\u50F9\u683C"] = trade.EntryPrice,
                ["\u9032\u5834Ratio"] = trade.EntryRatio,
                ["\u9032\u5834\u5916\u76E43\u79D2(M)"] = outsideVol3s,
                ["\u9032\u5834\u6642DayHigh"] = trade.DayHighAtEntry,
                ["\u51FA\u5834\u6279\u6B21"] = (object)"-",
                ["\u51FA\u5834\u985E\u578B"] = "\u672A\u51FA\u5834",                 // 未出場
                ["\u51FA\u5834\u6642\u9593"] = (object)"-",
                ["\u51FA\u5834\u50F9\u683C"] = (object)"-",
                ["\u51FA\u5834\u6BD4\u4F8B"] = (object)"-",
                ["\u51FA\u5834\u539F\u56E0"] = (object)"-",
                ["\u5BE6\u969B\u51FA\u5834\u5747\u50F9"] = trade.EntryPrice,
                ["\u7E3D\u640D\u76CA\u91D1\u984D"] = 0,
                ["\u7E3D\u640D\u76CA\u767E\u5206\u6BD4"] = 0
            });
        }

        /// <summary>
        /// Writes a list of dictionaries to CSV with UTF-8 BOM encoding.
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
                    if (keySet.Add(key))
                        allKeys.Add(key);
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
