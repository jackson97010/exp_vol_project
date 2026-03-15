using System;
using BacktestModule.Strategy;
using BacktestModule.Core.Models;
using System.Collections.Generic;
using BacktestModule.Strategy;
using BacktestModule.Core.Models;
using System.Globalization;
using BacktestModule.Strategy;
using BacktestModule.Core.Models;
using System.IO;
using BacktestModule.Strategy;
using BacktestModule.Core.Models;
using System.Linq;
using BacktestModule.Strategy;
using BacktestModule.Core.Models;
using System.Text;
using System.Diagnostics;
using BacktestModule.Strategy;
using BacktestModule.Core.Models;
using Microsoft.Extensions.Logging;
using BacktestModule.Core;

namespace BacktestModule.Visualization
{
    /// <summary>
    /// Chart creator for comprehensive strategy visualization.
    /// Produces interactive HTML charts with 3 subplots using embedded Plotly.js:
    /// 1. Price chart (50%): price, day high, entry/exit markers, ref price, limit-up, signal markers
    /// 2. Ratio chart (25%): dual lines (ratio_15s_300s + ratio_15s_180s_w321), entry markers
    /// 3. Change % chart (25%): price change from reference price
    /// </summary>
    public class ChartCreator : IChartCreator
    {
        private readonly Dictionary<string, object> _config;
        private readonly ILogger<ChartCreator> _logger;

        /// <summary>
        /// Initializes the chart creator.
        /// </summary>
        /// <param name="config">Configuration dictionary. Can be null.</param>
        /// <param name="logger">Logger instance. Can be null.</param>
        public ChartCreator(
            Dictionary<string, object> config = null,
            ILogger<ChartCreator> logger = null)
        {
            _config = config ?? new Dictionary<string, object>();
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ChartCreator>.Instance;
        }

        /// <summary>
        /// Creates the full strategy visualization chart.
        /// </summary>
        /// <param name="data">Tick data list.</param>
        /// <param name="trades">Trade records as dictionaries.</param>
        /// <param name="outputPath">HTML output path.</param>
        /// <param name="pngOutputPath">PNG output path (optional; PNG generation requires external tool).</param>
        /// <param name="refPrice">Reference price (previous close).</param>
        /// <param name="limitUpPrice">Limit-up price.</param>
        /// <param name="stockId">Stock code.</param>
        /// <param name="companyName">Company name.</param>
        /// <returns>Output path.</returns>
        public string CreateStrategyChart(
            List<TickData> data,
            List<Dictionary<string, object>> trades,
            string outputPath,
            string pngOutputPath = null,
            double? refPrice = null,
            double? limitUpPrice = null,
            string stockId = null,
            string companyName = null,
            string subtitleInfo = null)
        {
            // Filter out price=0 data (depth-only rows) for price chart
            var dfPrice = data.Where(d => d.Price > 0).ToList();

            if (dfPrice.Count < 10)
            {
                _logger.LogWarning("Filtered data too few ({Count} rows), using raw data.", dfPrice.Count);
                dfPrice = new List<TickData>(data);
            }

            // Determine ratio column
            string ratioColumn = "ratio_15s_300s";
            if (_config.ContainsKey("ratio_column"))
                ratioColumn = _config["ratio_column"]?.ToString() ?? ratioColumn;

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head>");
            sb.AppendLine("<meta charset='utf-8'>");

            string titleText = BuildTitle(stockId, companyName);
            sb.AppendLine($"<title>{Sanitize(titleText)}</title>");
            sb.AppendLine("<script src='https://cdn.plot.ly/plotly-latest.min.js'></script>");
            sb.AppendLine("</head><body>");
            sb.AppendLine("<div id='chart' style='width:100%;height:900px;'></div>");
            sb.AppendLine("<script>");

            BuildAllSubplots(sb, data, dfPrice, trades, refPrice, limitUpPrice, stockId, companyName, ratioColumn, subtitleInfo);

            sb.AppendLine("</script>");
            sb.AppendLine("</body></html>");

            // Ensure output directory exists
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            _logger.LogInformation("Interactive chart saved to: {OutputPath}", outputPath);

            // PNG generation via Playwright screenshot
            if (!string.IsNullOrEmpty(pngOutputPath))
            {
                GeneratePng(outputPath, pngOutputPath);
            }

            return outputPath;
        }

        /// <summary>
        /// Generates a PNG screenshot of the HTML chart using Playwright (npx playwright screenshot).
        /// </summary>
        private void GeneratePng(string htmlPath, string pngPath)
        {
            try
            {
                // Convert to absolute path and build proper file:// URI (encodes # and other special chars)
                string fullHtmlPath = Path.GetFullPath(htmlPath);
                string fileUri = new Uri(fullHtmlPath).AbsoluteUri;
                string fullPngPath = Path.GetFullPath(pngPath);

                // Ensure output directory exists
                string pngDir = Path.GetDirectoryName(fullPngPath);
                if (!string.IsNullOrEmpty(pngDir))
                    Directory.CreateDirectory(pngDir);

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c npx playwright screenshot --full-page \"{fileUri}\" \"{fullPngPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    _logger.LogWarning("Failed to start Playwright process for PNG generation.");
                    return;
                }

                bool exited = process.WaitForExit(30_000); // 30 second timeout
                if (!exited)
                {
                    process.Kill();
                    _logger.LogWarning("Playwright screenshot timed out (30s). PNG not generated.");
                    return;
                }

                if (process.ExitCode == 0 && File.Exists(fullPngPath))
                {
                    _logger.LogInformation("PNG chart saved to: {PngPath}", fullPngPath);
                    System.Console.WriteLine($"[INFO] PNG chart saved to: {fullPngPath}");
                }
                else
                {
                    string stderr = process.StandardError.ReadToEnd();
                    _logger.LogWarning("Playwright screenshot failed (exit code {ExitCode}): {Error}",
                        process.ExitCode, stderr);
                    System.Console.WriteLine($"[WARNING] PNG generation failed: {stderr}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("PNG generation error: {Message}", ex.Message);
                System.Console.WriteLine($"[WARNING] PNG generation error: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds the Plotly.js script for 3 subplots matching Python layout:
        /// 1. Price (50%) - price, day high, ref price, limit-up, entry/exit markers
        /// 2. Ratio (25%) - dual lines: ratio_15s_300s + ratio_15s_180s_w321
        /// 3. Change % (25%) - price change from ref price
        /// </summary>
        private void BuildAllSubplots(
            StringBuilder sb,
            List<TickData> allData,
            List<TickData> priceData,
            List<Dictionary<string, object>> trades,
            double? refPrice, double? limitUpPrice,
            string stockId, string companyName,
            string ratioColumn,
            string subtitleInfo = null)
        {
            sb.AppendLine("var traces = [];");

            // Subplot 1: Price Chart
            AddPriceChart(sb, allData, priceData, trades, refPrice, limitUpPrice);

            // Subplot 2: Ratio Chart (dual lines)
            AddRatioChart(sb, allData, ratioColumn, trades);

            // Subplot 3: Change % from ref price
            AddChangePctChart(sb, priceData, refPrice);

            // Calculate price range for y-axis
            var validPrices = priceData.Where(d => d.Price > 0).Select(d => d.Price).ToList();
            double priceMin = validPrices.Count > 0 ? validPrices.Min() : 0;
            double priceMax = validPrices.Count > 0 ? validPrices.Max() : 100;
            double priceMargin = (priceMax - priceMin) * 0.1;

            // Layout
            string titleText = BuildTitle(stockId, companyName);
            if (!string.IsNullOrEmpty(subtitleInfo))
                titleText += $"<br><sup>{Sanitize(subtitleInfo)}</sup>";
            sb.AppendLine("var layout = {");
            sb.AppendLine($"  title: {{text:'{Sanitize(titleText)}', font:{{size:18}}}},");
            sb.AppendLine("  showlegend: true,");
            sb.AppendLine("  legend: {orientation:'h', yanchor:'bottom', y:1.02, xanchor:'right', x:1},");
            sb.AppendLine("  height: 900,");
            sb.AppendLine("  hovermode: 'x unified',");
            sb.AppendLine("  template: 'plotly_white',");

            // 3-subplot domain: Price 50%, Ratio 25%, Change% 25%
            sb.AppendLine("  xaxis:  {showticklabels:false, showgrid:true, gridcolor:'lightgray', domain:[0,1], anchor:'y'},");
            sb.AppendLine($"  yaxis:  {{title:'Price', domain:[0.55,1.0], range:[{Fmt(priceMin - priceMargin)},{Fmt(priceMax + priceMargin)}]}},");
            sb.AppendLine("  xaxis2: {showticklabels:false, showgrid:true, gridcolor:'lightgray', domain:[0,1], anchor:'y2', matches:'x'},");
            sb.AppendLine("  yaxis2: {title:'Ratio', domain:[0.28,0.50]},");
            sb.AppendLine("  xaxis3: {tickformat:'%H:%M:%S', title:'Time', showgrid:true, gridcolor:'lightgray', domain:[0,1], anchor:'y3', matches:'x'},");
            sb.AppendLine("  yaxis3: {title:'Change %', domain:[0.0,0.22]},");

            // Shapes for threshold lines
            sb.AppendLine("  shapes: [");
            AddShapes(sb, refPrice, limitUpPrice);
            sb.AppendLine("  ]");
            sb.AppendLine("};");

            sb.AppendLine("Plotly.newPlot('chart', traces, layout, {displayModeBar:true, scrollZoom:true});");
        }

        /// <summary>
        /// Adds price chart traces (subplot 1).
        /// </summary>
        private void AddPriceChart(
            StringBuilder sb,
            List<TickData> allData,
            List<TickData> priceData,
            List<Dictionary<string, object>> trades,
            double? refPrice, double? limitUpPrice)
        {
            // Filter valid data with both price > 0 and day_high > 0
            var validData = priceData.Where(d => d.Price > 0 && d.DayHigh > 0).ToList();
            if (validData.Count == 0)
            {
                _logger.LogWarning("No valid price data to display.");
                return;
            }

            var times = validData.Select(d => $"'{FmtTime(d.Time)}'").ToList();
            var prices = validData.Select(d => Fmt(d.Price)).ToList();
            var dayHighs = validData.Select(d => Fmt(d.DayHigh)).ToList();

            // Price line (black)
            sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(prices)}], mode:'lines', name:'Price', line:{{color:'black',width:1}}, xaxis:'x', yaxis:'y'}});");

            // Day High line (orange dotted, matching Python)
            sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(dayHighs)}], mode:'lines', name:'Day High', line:{{color:'#FF9800',width:2,dash:'dot'}}, xaxis:'x', yaxis:'y'}});");

            // Trade markers
            AddTradeMarkers(sb, trades);

            // Observation signal markers
            AddSignalMarkers(sb, allData);
        }

        /// <summary>
        /// Adds all trade markers (entry, trailing stops, partial exits, reentry, final exit).
        /// </summary>
        private void AddTradeMarkers(StringBuilder sb, List<Dictionary<string, object>> trades)
        {
            for (int i = 0; i < trades.Count; i++)
            {
                var trade = trades[i];
                bool showLegend = (i == 0);
                string sl = showLegend ? "true" : "false";

                // Entry point
                string entryTime = $"'{FmtTime(GetObj(trade, "entry_time"))}'";
                string entryPrice = Fmt(GetDbl(trade, "entry_price"));
                double entryRatio = GetDbl(trade, "entry_ratio");
                sb.AppendLine($"traces.push({{x:[{entryTime}], y:[{entryPrice}], mode:'markers', name:{(showLegend ? "'Entry'" : "null")}, showlegend:{sl}, marker:{{color:'red',size:15,symbol:'circle'}}, hovertemplate:'Entry<br>Price: {entryPrice}<br>Ratio: {entryRatio:F1}<br>Time: %{{x}}', xaxis:'x', yaxis:'y'}});");

                // Check for trailing stop mode
                bool hasTrailingExits = trade.ContainsKey("trailing_exit_details")
                    && trade["trailing_exit_details"] is List<Dictionary<string, object>> trailingExits
                    && trailingExits.Count > 0;

                if (hasTrailingExits)
                {
                    var trailingDetails = (List<Dictionary<string, object>>)trade["trailing_exit_details"];
                    var exitColors = new Dictionary<string, string> { ["1min"] = "green", ["3min"] = "orange", ["5min"] = "purple" };

                    foreach (var exitDetail in trailingDetails)
                    {
                        string level = GetStr(exitDetail, "level");
                        string exitColor = exitColors.ContainsKey(level) ? exitColors[level] : "gray";
                        string exitTime = $"'{FmtTime(GetObj(exitDetail, "time"))}'";
                        double exitPriceVal = GetDbl(exitDetail, "price");
                        double exitRatioVal = GetDbl(exitDetail, "ratio");

                        sb.AppendLine($"traces.push({{x:[{exitTime}], y:[{Fmt(exitPriceVal)}], mode:'markers', name:{(showLegend ? $"'Trailing Stop {level}'" : "null")}, showlegend:{sl}, marker:{{color:'{exitColor}',size:15,symbol:'triangle-down'}}, hovertemplate:'Trailing Stop ({level})<br>Price: {Fmt(exitPriceVal)}<br>Ratio: {exitRatioVal:P0}<br>Time: %{{x}}', xaxis:'x', yaxis:'y'}});");
                    }

                    // Final exit if remaining position
                    double totalExitRatio = trailingDetails.Sum(e => GetDbl(e, "ratio"));
                    if (trade.ContainsKey("final_exit_time") && GetObj(trade, "final_exit_time") != null && totalExitRatio < 0.99)
                    {
                        string finalReason = GetStr(trade, "final_exit_reason");
                        string finalLabel = finalReason.Contains("\u9032\u5834\u50F9\u4FDD\u8B77") ? "Entry Price Protection" : "Close All";
                        string finalTime = $"'{FmtTime(GetObj(trade, "final_exit_time"))}'";
                        double finalPrice = GetDbl(trade, "final_exit_price");

                        sb.AppendLine($"traces.push({{x:[{finalTime}], y:[{Fmt(finalPrice)}], mode:'markers', name:{(showLegend ? $"'{finalLabel}'" : "null")}, showlegend:{sl}, marker:{{color:'green',size:15,symbol:'circle'}}, hovertemplate:'{finalLabel}<br>Price: {Fmt(finalPrice)}<br>Time: %{{x}}', xaxis:'x', yaxis:'y'}});");
                    }
                }
                else
                {
                    // Check for limit-up exit
                    string partialReason = GetStr(trade, "partial_exit_reason");
                    bool isLimitUpExit = trade.ContainsKey("partial_exit_time")
                        && GetObj(trade, "partial_exit_time") != null
                        && partialReason.Contains("\u6F32\u505C");  // 漲停

                    if (isLimitUpExit)
                    {
                        string exitTime = $"'{FmtTime(GetObj(trade, "partial_exit_time"))}'";
                        double exitPriceVal = GetDbl(trade, "partial_exit_price");
                        sb.AppendLine($"traces.push({{x:[{exitTime}], y:[{Fmt(exitPriceVal)}], mode:'markers', name:{(showLegend ? "'Limit Up Exit'" : "null")}, showlegend:{sl}, marker:{{color:'orange',size:15,symbol:'triangle-down'}}, hovertemplate:'Limit Up Exit (50%)<br>Price: {Fmt(exitPriceVal)}<br>Time: %{{x}}', xaxis:'x', yaxis:'y'}});");
                    }
                    else
                    {
                        // Partial exit
                        if (trade.ContainsKey("partial_exit_time") && GetObj(trade, "partial_exit_time") != null)
                        {
                            string exitTime = $"'{FmtTime(GetObj(trade, "partial_exit_time"))}'";
                            double exitPriceVal = GetDbl(trade, "partial_exit_price");
                            sb.AppendLine($"traces.push({{x:[{exitTime}], y:[{Fmt(exitPriceVal)}], mode:'markers', name:{(showLegend ? "'Reduce 50%'" : "null")}, showlegend:{sl}, marker:{{color:'orange',size:15,symbol:'triangle-down'}}, hovertemplate:'Reduce 50%<br>Price: {Fmt(exitPriceVal)}<br>Time: %{{x}}', xaxis:'x', yaxis:'y'}});");
                        }

                        // Reentry
                        if (trade.ContainsKey("reentry_time") && GetObj(trade, "reentry_time") != null)
                        {
                            string reTime = $"'{FmtTime(GetObj(trade, "reentry_time"))}'";
                            double rePriceVal = GetDbl(trade, "reentry_price");
                            sb.AppendLine($"traces.push({{x:[{reTime}], y:[{Fmt(rePriceVal)}], mode:'markers', name:{(showLegend ? "'Reentry'" : "null")}, showlegend:{sl}, marker:{{color:'blue',size:15,symbol:'triangle-up'}}, hovertemplate:'Reentry<br>Price: {Fmt(rePriceVal)}<br>Time: %{{x}}', xaxis:'x', yaxis:'y'}});");
                        }

                        // Final exit
                        if (trade.ContainsKey("final_exit_time") && GetObj(trade, "final_exit_time") != null)
                        {
                            bool isReentryExit = trade.ContainsKey("reentry_exit_reason") && GetObj(trade, "reentry_exit_reason") != null;
                            string exitLabel = isReentryExit ? "Reentry Stop" : "Close All";
                            string finalTime = $"'{FmtTime(GetObj(trade, "final_exit_time"))}'";
                            double finalPrice = GetDbl(trade, "final_exit_price");

                            sb.AppendLine($"traces.push({{x:[{finalTime}], y:[{Fmt(finalPrice)}], mode:'markers', name:{(showLegend ? $"'{exitLabel}'" : "null")}, showlegend:{sl}, marker:{{color:'green',size:15,symbol:'circle'}}, hovertemplate:'{exitLabel}<br>Price: {Fmt(finalPrice)}<br>Time: %{{x}}', xaxis:'x', yaxis:'y'}});");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Adds observation signal markers to the price chart.
        /// Gold diamonds for volume shrink signals, purple stars for VWAP deviation signals.
        /// </summary>
        private void AddSignalMarkers(StringBuilder sb, List<TickData> data)
        {
            // Volume shrink signal: gold diamond
            var vsData = data.Where(d => d.VolumeShrinkSignal && d.Price > 0).ToList();
            if (vsData.Count > 0)
            {
                var vsTimes = vsData.Select(d => $"'{FmtTime(d.Time)}'").ToList();
                var vsPrices = vsData.Select(d => Fmt(d.Price)).ToList();
                sb.AppendLine($"traces.push({{x:[{J(vsTimes)}], y:[{J(vsPrices)}], mode:'markers', name:'Vol Shrink', marker:{{color:'gold',size:10,symbol:'diamond',line:{{color:'black',width:1}}}}, hovertemplate:'價漲量縮<br>Price: %{{y}}<br>Time: %{{x}}', xaxis:'x', yaxis:'y'}});");
            }

            // VWAP deviation signal: purple star
            var vdData = data.Where(d => d.VwapDeviationSignal && d.Price > 0).ToList();
            if (vdData.Count > 0)
            {
                var vdTimes = vdData.Select(d => $"'{FmtTime(d.Time)}'").ToList();
                var vdPrices = vdData.Select(d => Fmt(d.Price)).ToList();
                sb.AppendLine($"traces.push({{x:[{J(vdTimes)}], y:[{J(vdPrices)}], mode:'markers', name:'VWAP Deviation', marker:{{color:'purple',size:10,symbol:'star',line:{{color:'black',width:1}}}}, hovertemplate:'VWAP乖離<br>Price: %{{y}}<br>Time: %{{x}}', xaxis:'x', yaxis:'y'}});");
            }
        }

        /// <summary>
        /// Adds ratio chart traces (subplot 2) with dual lines matching Python layout.
        /// Blue solid: ratio_15s_300s, Purple dotted: ratio_15s_180s_w321
        /// </summary>
        private void AddRatioChart(StringBuilder sb, List<TickData> data, string ratioColumn,
            List<Dictionary<string, object>> trades = null)
        {
            var times = data.Select(d => $"'{FmtTime(d.Time)}'").ToList();

            // Line 1: ratio_15s_300s (blue solid)
            var ratios300s = data.Select(d => Fmt(d.Ratio15s300s)).ToList();
            sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(ratios300s)}], mode:'lines', name:'15s_300s', line:{{color:'#2196F3',width:1.5}}, xaxis:'x2', yaxis:'y2'}});");

            // Line 2: ratio_15s_180s_w321 (purple dotted) - if available
            bool hasW321 = data.Any(d => d.Ratio15s180sW321 != 0);
            if (hasW321)
            {
                var ratiosW321 = data.Select(d => d.Ratio15s180sW321 != 0 ? Fmt(d.Ratio15s180sW321) : "null").ToList();
                sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(ratiosW321)}], mode:'lines', name:'15s_180s_w321', line:{{color:'#9C27B0',width:1,dash:'dot'}}, xaxis:'x2', yaxis:'y2'}});");
            }

            // Entry markers on ratio chart
            if (trades != null)
            {
                for (int i = 0; i < trades.Count; i++)
                {
                    var trade = trades[i];
                    bool showLegend = (i == 0);
                    string entryTimeVal = FmtTime(GetObj(trade, "entry_time"));
                    double entryRatio = GetDbl(trade, "entry_ratio");

                    if (entryRatio > 0)
                    {
                        sb.AppendLine($"traces.push({{x:['{entryTimeVal}'], y:[{Fmt(entryRatio)}], mode:'markers', name:'Entry (Ratio)', marker:{{symbol:'circle',size:15,color:'red',line:{{color:'white',width:1}}}}, showlegend:{(showLegend ? "true" : "false")}, xaxis:'x2', yaxis:'y2'}});");
                    }
                }
            }
        }

        /// <summary>
        /// Adds change percentage chart (subplot 3) matching Python layout.
        /// Cyan line showing price change from ref_price.
        /// </summary>
        private void AddChangePctChart(StringBuilder sb, List<TickData> priceData, double? refPrice)
        {
            if (!refPrice.HasValue || refPrice.Value <= 0) return;

            var validData = priceData.Where(d => d.Price > 0).ToList();
            if (validData.Count == 0) return;

            var times = validData.Select(d => $"'{FmtTime(d.Time)}'").ToList();
            var changePcts = validData.Select(d => Fmt((d.Price - refPrice.Value) / refPrice.Value * 100)).ToList();

            sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(changePcts)}], mode:'lines', name:'Change %', line:{{color:'#00BCD4',width:1}}, xaxis:'x3', yaxis:'y3'}});");
        }

        /// <summary>
        /// Adds threshold shapes (horizontal lines) to the 3-subplot layout.
        /// </summary>
        private void AddShapes(StringBuilder sb, double? refPrice, double? limitUpPrice)
        {
            // Ref price (subplot 1)
            if (refPrice.HasValue && refPrice.Value > 0)
                sb.AppendLine($"    {{type:'line', xref:'paper', x0:0, x1:1, yref:'y', y0:{Fmt(refPrice.Value)}, y1:{Fmt(refPrice.Value)}, line:{{color:'gray',dash:'dash'}}}},");

            // Limit-up price (subplot 1)
            if (limitUpPrice.HasValue && limitUpPrice.Value > 0)
                sb.AppendLine($"    {{type:'line', xref:'paper', x0:0, x1:1, yref:'y', y0:{Fmt(limitUpPrice.Value)}, y1:{Fmt(limitUpPrice.Value)}, line:{{color:'red',dash:'dash'}}}},");

            // Ratio = 1.0 reference (subplot 2)
            sb.AppendLine("    {type:'line', xref:'paper', x0:0, x1:1, yref:'y2', y0:1.0, y1:1.0, line:{color:'gray',dash:'dash'}},");

            // Ratio = 5.5 threshold (subplot 2)
            sb.AppendLine("    {type:'line', xref:'paper', x0:0, x1:1, yref:'y2', y0:5.5, y1:5.5, line:{color:'orange',dash:'dot'}},");

            // Change % = 0 (subplot 3)
            sb.AppendLine("    {type:'line', xref:'paper', x0:0, x1:1, yref:'y3', y0:0, y1:0, line:{color:'gray',dash:'dash'}},");

            // Change % = 10% limit-up (subplot 3)
            sb.AppendLine("    {type:'line', xref:'paper', x0:0, x1:1, yref:'y3', y0:10.0, y1:10.0, line:{color:'red',dash:'dot'}}");
        }

        /// <summary>
        /// Builds the chart title string.
        /// </summary>
        private static string BuildTitle(string stockId, string companyName)
        {
            if (!string.IsNullOrEmpty(stockId) && !string.IsNullOrEmpty(companyName))
                return $"{stockId} {companyName} 日內走勢圖";
            if (!string.IsNullOrEmpty(stockId))
                return $"{stockId} 日內走勢圖";
            return "日內走勢圖";
        }

        // --- Helper methods ---

        private static string FmtTime(object time)
        {
            if (time is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            return time?.ToString() ?? "";
        }

        private static string Fmt(double value) => value.ToString("G", CultureInfo.InvariantCulture);

        private static string J(List<string> items) => string.Join(",", items);

        private static string Sanitize(string s) => s?.Replace("'", "\\'") ?? "";

        private static object GetObj(Dictionary<string, object> dict, string key)
            => dict.ContainsKey(key) ? dict[key] : null;

        private static double GetDbl(Dictionary<string, object> dict, string key)
            => dict.ContainsKey(key) && dict[key] != null ? Convert.ToDouble(dict[key]) : 0;

        private static string GetStr(Dictionary<string, object> dict, string key)
            => dict.ContainsKey(key) && dict[key] != null ? dict[key].ToString() : "";
    }

    /// <summary>
    /// Compatibility function: Creates exit visualization using ChartCreator.
    /// </summary>
    public static class ChartCreatorExtensions
    {
        /// <summary>
        /// Creates an exit visualization chart (compatibility wrapper).
        /// </summary>
        public static string CreateExitVisualization(
            List<TickData> data,
            List<Dictionary<string, object>> trades,
            string outputPath,
            Dictionary<string, object> config = null,
            string pngOutputPath = null)
        {
            var creator = new ChartCreator(config);
            return creator.CreateStrategyChart(data, trades, outputPath, pngOutputPath);
        }
    }
}
