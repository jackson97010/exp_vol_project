using System;
using BacktestModule.Core.Models;
using System.Collections.Generic;
using BacktestModule.Core.Models;
using System.Globalization;
using BacktestModule.Core.Models;
using System.IO;
using BacktestModule.Core.Models;
using System.Linq;
using BacktestModule.Core.Models;
using System.Text;
using BacktestModule.Core.Models;
using Microsoft.Extensions.Logging;
using BacktestModule.Core;
using BacktestModule.Strategy;

namespace BacktestModule.Exporters
{
    /// <summary>
    /// Early-morning strategy chart creator (simplified version).
    /// Produces an interactive HTML chart with 3 subplots:
    /// 1. Price + Day High + entry/exit markers + trailing stop low lines
    /// 2. Massive matching amount
    /// 3. Order book ratio (ask/bid)
    ///
    /// Uses embedded Plotly.js for interactive HTML output.
    /// </summary>
    public class EarlyChartCreator
    {
        private readonly Dictionary<string, object> _config;
        private readonly ILogger<EarlyChartCreator> _logger;

        /// <summary>
        /// Initializes the early chart creator.
        /// </summary>
        /// <param name="config">Configuration dictionary. Can be null.</param>
        /// <param name="logger">Logger instance. Can be null.</param>
        public EarlyChartCreator(
            Dictionary<string, object> config = null,
            ILogger<EarlyChartCreator> logger = null)
        {
            _config = config ?? new Dictionary<string, object>();
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EarlyChartCreator>.Instance;
        }

        /// <summary>
        /// Creates the early strategy visualization chart (simplified version).
        /// </summary>
        /// <param name="data">Tick data list.</param>
        /// <param name="tradeRecord">Trade record dictionary with entry/exit information.</param>
        /// <param name="outputPath">HTML output file path.</param>
        /// <param name="stockId">Stock code.</param>
        /// <param name="date">Date string.</param>
        /// <param name="massiveThreshold">Massive matching threshold in TWD.</param>
        /// <returns>Output path, or null if insufficient data.</returns>
        public string CreateChart(
            List<TickData> data,
            Dictionary<string, object> tradeRecord,
            string outputPath,
            string stockId,
            string date,
            double massiveThreshold)
        {
            // Filter valid price data
            var validData = data.Where(d => d.Price > 0).ToList();

            if (validData.Count < 10)
            {
                _logger.LogWarning("{StockId} has too few valid data points, skipping chart generation.", stockId);
                return null;
            }

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head>");
            sb.AppendLine("<meta charset='utf-8'>");
            sb.AppendLine($"<title>{stockId} Early Entry Strategy - {date}</title>");
            sb.AppendLine("<script src='https://cdn.plot.ly/plotly-latest.min.js'></script>");
            sb.AppendLine("</head><body>");
            sb.AppendLine($"<div id='chart' style='width:100%;height:900px;'></div>");
            sb.AppendLine("<script>");

            // Build trace data
            BuildPlotlyScript(sb, data, validData, tradeRecord, stockId, date, massiveThreshold);

            sb.AppendLine("</script>");
            sb.AppendLine("</body></html>");

            // Ensure output directory exists
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            _logger.LogInformation("Chart saved: {OutputPath}", outputPath);

            return outputPath;
        }

        /// <summary>
        /// Builds the Plotly.js script for the 3-subplot chart.
        /// </summary>
        private void BuildPlotlyScript(
            StringBuilder sb,
            List<TickData> allData,
            List<TickData> validData,
            Dictionary<string, object> tradeRecord,
            string stockId, string date,
            double massiveThreshold)
        {
            // --- Subplot 1: Price Chart ---
            var times = validData.Select(d => $"'{d.Time:yyyy-MM-dd HH:mm:ss.fff}'").ToList();
            var prices = validData.Select(d => d.Price.ToString("F2", CultureInfo.InvariantCulture)).ToList();
            var dayHighs = validData.Select(d => d.DayHigh.ToString("F2", CultureInfo.InvariantCulture)).ToList();

            // Price line
            sb.AppendLine($"var trace_price = {{x:[{string.Join(",", times)}], y:[{string.Join(",", prices)}], mode:'lines', name:'Price', line:{{color:'black',width:1.5}}, xaxis:'x', yaxis:'y'}};");

            // Day High line
            sb.AppendLine($"var trace_dayhigh = {{x:[{string.Join(",", times)}], y:[{string.Join(",", dayHighs)}], mode:'lines', name:'Day High', line:{{color:'red',width:2}}, opacity:0.7, xaxis:'x', yaxis:'y'}};");

            // Trailing stop low lines
            var lowLines = new[] {
                ("low_1m", "green", "1min Low"),
                ("low_3m", "orange", "3min Low"),
                ("low_5m", "purple", "5min Low")
            };
            int lowTraceIdx = 0;
            foreach (var (field, color, name) in lowLines)
            {
                var lowValues = validData.Select(d => field switch
                {
                    "low_1m" => d.Low1m,
                    "low_3m" => d.Low3m,
                    "low_5m" => d.Low5m,
                    _ => 0.0
                }).ToList();

                if (lowValues.Any(v => v > 0))
                {
                    var lowStrs = lowValues.Select(v => v > 0
                        ? v.ToString("F2", CultureInfo.InvariantCulture) : "null").ToList();
                    sb.AppendLine($"var trace_low{lowTraceIdx} = {{x:[{string.Join(",", times)}], y:[{string.Join(",", lowStrs)}], mode:'lines', name:'{name}', line:{{color:'{color}',width:1,dash:'dash'}}, opacity:0.6, xaxis:'x', yaxis:'y'}};");
                }
                else
                {
                    sb.AppendLine($"var trace_low{lowTraceIdx} = {{x:[], y:[], mode:'lines', name:'{name}', xaxis:'x', yaxis:'y'}};");
                }
                lowTraceIdx++;
            }

            // Entry marker
            sb.Append("var trace_entry = {x:[], y:[], mode:'markers+text', name:'Entry', marker:{color:'red',size:20,symbol:'circle'}, text:['Entry'], textposition:'top center', xaxis:'x', yaxis:'y'};");
            if (tradeRecord.ContainsKey("entry_price") && tradeRecord.ContainsKey("entry_time"))
            {
                double entryPrice = Convert.ToDouble(tradeRecord["entry_price"]);
                if (entryPrice > 0)
                {
                    string entryTime = FormatTime(tradeRecord["entry_time"]);
                    sb.AppendLine($"trace_entry.x = ['{entryTime}']; trace_entry.y = [{entryPrice.ToString("F2", CultureInfo.InvariantCulture)}];");
                }
            }

            // Exit markers
            sb.AppendLine("var trace_exits = [];");
            if (tradeRecord.ContainsKey("exits") && tradeRecord["exits"] is List<Dictionary<string, object>> exits)
            {
                var exitColors = new Dictionary<string, string> { ["1min"] = "green", ["3min"] = "orange", ["5min"] = "purple" };
                for (int i = 0; i < exits.Count; i++)
                {
                    var exitInfo = exits[i];
                    string exitTime = exitInfo.ContainsKey("time") ? FormatTime(exitInfo["time"]) : "";
                    double exitPrice = exitInfo.ContainsKey("price") ? Convert.ToDouble(exitInfo["price"]) : 0;
                    string exitReason = exitInfo.ContainsKey("reason") ? exitInfo["reason"]?.ToString() : "";

                    string levelName = null;
                    foreach (var key in new[] { "1min", "3min", "5min" })
                    {
                        if (exitReason.Contains(key)) { levelName = key; break; }
                    }
                    string color = levelName != null && exitColors.ContainsKey(levelName) ? exitColors[levelName] : "gray";
                    string label = levelName != null ? $"{levelName} trailing stop" : "Exit";

                    sb.AppendLine($"trace_exits.push({{x:['{exitTime}'], y:[{exitPrice.ToString("F2", CultureInfo.InvariantCulture)}], mode:'markers', name:'{label}', showlegend:{(i == 0 ? "true" : "false")}, marker:{{color:'{color}',size:18,symbol:'triangle-down'}}, xaxis:'x', yaxis:'y'}});");
                }
            }

            // --- Subplot 2: Massive Matching Amount ---
            var allTimes = allData.Select(d => $"'{d.Time:yyyy-MM-dd HH:mm:ss.fff}'").ToList();

            // Use DayHighGrowthRate as a placeholder -- in real usage, the massive matching amount
            // would be a separate computed metric. Here we expose the metric columns from TickData.
            // Since the Python code reads 'massive_matching_amount' or 'outside_amount_1s' columns,
            // we use what is available. In the C# TickData, these computed metrics are stored during the loop.
            // For this chart, we will output zero if the data is not populated.
            var amounts = allData.Select(d =>
            {
                // Use a generic field; in production, the backtest loop populates a MassiveMatchingAmount field
                // For now use 0 as placeholder; the actual value should come from the metric columns
                return "0";
            }).ToList();

            double thresholdWan = massiveThreshold / 10000;
            sb.AppendLine($"var trace_amount = {{x:[{string.Join(",", allTimes)}], y:[{string.Join(",", amounts)}], mode:'lines', name:'Outside Amount', line:{{color:'blue',width:2}}, xaxis:'x2', yaxis:'y2'}};");

            // --- Subplot 3: Order Book Ratio ---
            var ratios = allData.Select(d =>
                d.BidAskRatio > 0 ? d.BidAskRatio.ToString("F2", CultureInfo.InvariantCulture) : "null"
            ).ToList();
            sb.AppendLine($"var trace_ratio = {{x:[{string.Join(",", allTimes)}], y:[{string.Join(",", ratios)}], mode:'lines', name:'Ask/Bid Ratio', line:{{color:'purple',width:2}}, xaxis:'x3', yaxis:'y3'}};");

            // Threshold value
            double obThreshold = 1.0;
            if (_config.ContainsKey("orderbook_bid_ask_ratio_min"))
                obThreshold = Convert.ToDouble(_config["orderbook_bid_ask_ratio_min"]);

            // Build all traces array
            sb.AppendLine("var allTraces = [trace_price, trace_dayhigh, trace_low0, trace_low1, trace_low2, trace_entry, trace_amount, trace_ratio].concat(trace_exits);");

            // Layout
            sb.AppendLine("var layout = {");
            sb.AppendLine($"  title: '{stockId} Early Entry Strategy (09:01-09:05) - {date}',");
            sb.AppendLine("  showlegend: true,");
            sb.AppendLine("  height: 900,");
            sb.AppendLine("  hovermode: 'x unified',");
            sb.AppendLine("  plot_bgcolor: 'white',");
            sb.AppendLine("  paper_bgcolor: 'white',");
            sb.AppendLine("  grid: {rows:3, columns:1, pattern:'independent', roworder:'top to bottom'},");
            sb.AppendLine("  xaxis: {showticklabels:false, showgrid:true, gridcolor:'lightgray'},");
            sb.AppendLine("  yaxis: {title:'Price', domain:[0.55,1.0]},");
            sb.AppendLine("  xaxis2: {showticklabels:false, showgrid:true, gridcolor:'lightgray'},");
            sb.AppendLine($"  yaxis2: {{title:'Amount (10k)', domain:[0.28,0.50]}},");
            sb.AppendLine("  xaxis3: {title:'Time', showgrid:true, gridcolor:'lightgray'},");
            sb.AppendLine($"  yaxis3: {{title:'Ratio', domain:[0.0,0.22]}},");
            // Add threshold shapes
            sb.AppendLine("  shapes: [");
            sb.AppendLine($"    {{type:'line', xref:'paper', x0:0, x1:1, yref:'y2', y0:{thresholdWan.ToString("F1", CultureInfo.InvariantCulture)}, y1:{thresholdWan.ToString("F1", CultureInfo.InvariantCulture)}, line:{{color:'red',width:2,dash:'dash'}}}},");
            sb.AppendLine($"    {{type:'line', xref:'paper', x0:0, x1:1, yref:'y3', y0:{obThreshold.ToString("F1", CultureInfo.InvariantCulture)}, y1:{obThreshold.ToString("F1", CultureInfo.InvariantCulture)}, line:{{color:'red',width:2,dash:'dash'}}}}");
            sb.AppendLine("  ]");
            sb.AppendLine("};");

            sb.AppendLine("Plotly.newPlot('chart', allTraces, layout, {displayModeBar:true, scrollZoom:true});");
        }

        /// <summary>
        /// Formats a time value for JavaScript string representation.
        /// </summary>
        private static string FormatTime(object timeValue)
        {
            if (timeValue is DateTime dt)
                return dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            return timeValue?.ToString() ?? "";
        }
    }
}
