using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using BacktestModule.Core;

namespace BacktestModule.Visualization
{
    /// <summary>
    /// Chart creator for comprehensive strategy visualization.
    /// Produces interactive HTML charts with 5 subplots using embedded Plotly.js:
    /// 1. Price chart (price, day high, 1/3/5 min low lines, entry/exit markers, ref price, limit-up)
    /// 2. Ratio chart (ratio indicator, entry threshold, entry-allowed shaded area)
    /// 3. Day High growth rate (growth rate %, 0.86% threshold, exhaustion shaded area)
    /// 4. Orderbook thickness (bid/ask average volumes, thin/normal threshold lines)
    /// 5. Balance ratio (buy/sell balance ratio, 1.0 reference, balanced zone)
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
            string companyName = null)
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
            sb.AppendLine("<div id='chart' style='width:100%;height:1200px;'></div>");
            sb.AppendLine("<script>");

            BuildAllSubplots(sb, data, dfPrice, trades, refPrice, limitUpPrice, stockId, companyName, ratioColumn);

            sb.AppendLine("</script>");
            sb.AppendLine("</body></html>");

            // Ensure output directory exists
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            _logger.LogInformation("Interactive chart saved to: {OutputPath}", outputPath);

            // PNG generation note: Requires external tool (e.g., Playwright, Puppeteer, or kaleido equivalent)
            if (!string.IsNullOrEmpty(pngOutputPath))
            {
                _logger.LogWarning("PNG generation requires an external rendering engine (kaleido equivalent). Skipping PNG.");
            }

            return outputPath;
        }

        /// <summary>
        /// Builds the Plotly.js script for all 5 subplots.
        /// </summary>
        private void BuildAllSubplots(
            StringBuilder sb,
            List<TickData> allData,
            List<TickData> priceData,
            List<Dictionary<string, object>> trades,
            double? refPrice, double? limitUpPrice,
            string stockId, string companyName,
            string ratioColumn)
        {
            sb.AppendLine("var traces = [];");

            // Subplot 1: Price Chart
            AddPriceChart(sb, allData, priceData, trades, refPrice, limitUpPrice);

            // Subplot 2: Ratio Chart
            AddRatioChart(sb, allData, ratioColumn);

            // Subplot 3: Day High Growth Rate
            AddGrowthRateChart(sb, allData);

            // Subplot 4: Orderbook Thickness
            AddOrderbookThicknessChart(sb, allData);

            // Subplot 5: Balance Ratio
            AddBalanceRatioChart(sb, allData);

            // Layout
            string titleText = BuildTitle(stockId, companyName);
            sb.AppendLine("var layout = {");
            sb.AppendLine($"  title: {{text:'{Sanitize(titleText)}', font:{{size:18}}, x:0.02}},");
            sb.AppendLine("  showlegend: true,");
            sb.AppendLine("  height: 1200,");
            sb.AppendLine("  hovermode: 'x unified',");
            sb.AppendLine("  plot_bgcolor: 'white',");
            sb.AppendLine("  paper_bgcolor: 'white',");

            // Subplots domain assignments
            // Row heights: [0.34, 0.18, 0.18, 0.15, 0.15]
            sb.AppendLine("  xaxis:  {showticklabels:true, showgrid:true, gridcolor:'lightgray', title:'Time', domain:[0,1], anchor:'y'},");
            sb.AppendLine("  yaxis:  {title:'Price', domain:[0.68,1.0]},");
            sb.AppendLine("  xaxis2: {showticklabels:false, showgrid:true, gridcolor:'lightgray', domain:[0,1], anchor:'y2'},");
            sb.AppendLine("  yaxis2: {title:'Ratio', domain:[0.50,0.65]},");
            sb.AppendLine("  xaxis3: {showticklabels:false, showgrid:true, gridcolor:'lightgray', domain:[0,1], anchor:'y3'},");
            sb.AppendLine("  yaxis3: {title:'DH Growth Rate', domain:[0.32,0.47]},");
            sb.AppendLine("  xaxis4: {showticklabels:false, showgrid:true, gridcolor:'lightgray', domain:[0,1], anchor:'y4'},");
            sb.AppendLine("  yaxis4: {title:'Orderbook Thickness', domain:[0.17,0.29]},");
            sb.AppendLine("  xaxis5: {showticklabels:true, showgrid:true, gridcolor:'lightgray', domain:[0,1], anchor:'y5'},");
            sb.AppendLine("  yaxis5: {title:'Balance Ratio', domain:[0.0,0.14]},");

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

            // Price line
            sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(prices)}], mode:'lines', name:'Price', line:{{color:'black',width:1}}, xaxis:'x', yaxis:'y'}});");

            // Day High line
            sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(dayHighs)}], mode:'lines', name:'Day High', line:{{color:'red',width:2}}, opacity:0.7, xaxis:'x', yaxis:'y'}});");

            // Reference price line
            if (refPrice.HasValue && refPrice.Value > 0)
            {
                string t0 = $"'{FmtTime(allData.First().Time)}'";
                string t1 = $"'{FmtTime(allData.Last().Time)}'";
                sb.AppendLine($"traces.push({{x:[{t0},{t1}], y:[{Fmt(refPrice.Value)},{Fmt(refPrice.Value)}], mode:'lines', name:'Prev Close', line:{{color:'blue',width:1.5,dash:'dash'}}, xaxis:'x', yaxis:'y'}});");
            }

            // Limit-up price line
            if (limitUpPrice.HasValue && limitUpPrice.Value > 0)
            {
                string t0 = $"'{FmtTime(allData.First().Time)}'";
                string t1 = $"'{FmtTime(allData.Last().Time)}'";
                sb.AppendLine($"traces.push({{x:[{t0},{t1}], y:[{Fmt(limitUpPrice.Value)},{Fmt(limitUpPrice.Value)}], mode:'lines', name:'Limit Up', line:{{color:'red',width:1.5,dash:'dot'}}, xaxis:'x', yaxis:'y'}});");
            }

            // Trailing stop low lines
            AddLowLine(sb, allData, d => d.Low1m, "1min Low", "green");
            AddLowLine(sb, allData, d => d.Low3m, "3min Low", "orange");
            AddLowLine(sb, allData, d => d.Low5m, "5min Low", "purple");

            // Trade markers
            AddTradeMarkers(sb, trades);
        }

        /// <summary>
        /// Adds a rolling low line to the price chart.
        /// </summary>
        private void AddLowLine(StringBuilder sb, List<TickData> data, Func<TickData, double> selector, string name, string color)
        {
            var values = data.Select(d => selector(d)).ToList();
            if (values.All(v => v <= 0)) return;

            var times = data.Select(d => $"'{FmtTime(d.Time)}'").ToList();
            var vals = values.Select(v => v > 0 ? Fmt(v) : "null").ToList();
            sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(vals)}], mode:'lines', name:'{name}', line:{{color:'{color}',width:1,dash:'dash'}}, opacity:0.6, xaxis:'x', yaxis:'y'}});");
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
        /// Adds ratio chart traces (subplot 2).
        /// </summary>
        private void AddRatioChart(StringBuilder sb, List<TickData> data, string ratioColumn)
        {
            var times = data.Select(d => $"'{FmtTime(d.Time)}'").ToList();
            var ratios = data.Select(d => Fmt(d.Ratio15s300s)).ToList();

            sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(ratios)}], mode:'lines', name:'Ratio ({ratioColumn})', line:{{color:'purple',width:1.5}}, xaxis:'x2', yaxis:'y2'}});");

            // Entry-allowed shaded area (ratio >= threshold)
            double ratioThreshold = 3.0;
            if (_config.ContainsKey("ratio_entry_threshold"))
                ratioThreshold = Convert.ToDouble(_config["ratio_entry_threshold"]);

            var ratioHigh = data.Select(d =>
                d.Ratio15s300s >= ratioThreshold ? Fmt(d.Ratio15s300s) : "null").ToList();
            sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(ratioHigh)}], fill:'tozeroy', mode:'none', name:'Entry Allowed', fillcolor:'rgba(0,255,0,0.2)', showlegend:true, xaxis:'x2', yaxis:'y2'}});");
        }

        /// <summary>
        /// Adds growth rate chart traces (subplot 3).
        /// </summary>
        private void AddGrowthRateChart(StringBuilder sb, List<TickData> data)
        {
            var times = data.Select(d => $"'{FmtTime(d.Time)}'").ToList();
            var rates = data.Select(d => Fmt(d.DayHighGrowthRate * 100)).ToList();

            sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(rates)}], mode:'lines', name:'DH Growth Rate (%)', line:{{color:'blue',width:1}}, xaxis:'x3', yaxis:'y3'}});");

            // Exhaustion shaded area (rate < 0.86%)
            var exhaustion = data.Select(d =>
                d.DayHighGrowthRate * 100 < 0.86 ? Fmt(d.DayHighGrowthRate * 100) : "null").ToList();
            sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(exhaustion)}], fill:'tozeroy', mode:'none', name:'Momentum Exhaustion', fillcolor:'rgba(255,0,0,0.2)', showlegend:true, xaxis:'x3', yaxis:'y3'}});");
        }

        /// <summary>
        /// Adds orderbook thickness chart traces (subplot 4).
        /// </summary>
        private void AddOrderbookThicknessChart(StringBuilder sb, List<TickData> data)
        {
            var times = data.Select(d => $"'{FmtTime(d.Time)}'").ToList();
            var bidVols = data.Select(d => Fmt(d.BidAvgVolume)).ToList();
            var askVols = data.Select(d => Fmt(d.AskAvgVolume)).ToList();

            sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(bidVols)}], mode:'lines', name:'Bid Avg Volume', line:{{color:'green',width:1.5}}, xaxis:'x4', yaxis:'y4'}});");
            sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(askVols)}], mode:'lines', name:'Ask Avg Volume', line:{{color:'red',width:1.5}}, xaxis:'x4', yaxis:'y4'}});");
        }

        /// <summary>
        /// Adds balance ratio chart traces (subplot 5).
        /// </summary>
        private void AddBalanceRatioChart(StringBuilder sb, List<TickData> data)
        {
            var times = data.Select(d => $"'{FmtTime(d.Time)}'").ToList();
            var balances = data.Select(d => Fmt(d.BalanceRatio)).ToList();

            sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(balances)}], mode:'lines', name:'Bid/Ask Balance', line:{{color:'purple',width:1.5}}, xaxis:'x5', yaxis:'y5'}});");
        }

        /// <summary>
        /// Adds threshold shapes (horizontal lines) to the layout.
        /// </summary>
        private void AddShapes(StringBuilder sb, double? refPrice, double? limitUpPrice)
        {
            // Ratio threshold (subplot 2)
            double ratioThreshold = 3.0;
            if (_config.ContainsKey("ratio_entry_threshold"))
                ratioThreshold = Convert.ToDouble(_config["ratio_entry_threshold"]);
            sb.AppendLine($"    {{type:'line', xref:'paper', x0:0, x1:1, yref:'y2', y0:{Fmt(ratioThreshold)}, y1:{Fmt(ratioThreshold)}, line:{{color:'red',width:1,dash:'dash'}}}},");

            // Growth rate 0.86% threshold (subplot 3)
            sb.AppendLine("    {type:'line', xref:'paper', x0:0, x1:1, yref:'y3', y0:0.86, y1:0.86, line:{color:'red',width:1,dash:'dash'}},");

            // Orderbook thin threshold 20 (subplot 4)
            sb.AppendLine("    {type:'line', xref:'paper', x0:0, x1:1, yref:'y4', y0:20, y1:20, line:{color:'gray',width:1,dash:'dash'}},");
            // Orderbook normal threshold 40
            sb.AppendLine("    {type:'line', xref:'paper', x0:0, x1:1, yref:'y4', y0:40, y1:40, line:{color:'gray',width:1,dash:'solid'}},");

            // Balance ratio 1.0 (subplot 5)
            sb.AppendLine("    {type:'line', xref:'paper', x0:0, x1:1, yref:'y5', y0:1.0, y1:1.0, line:{color:'black',width:1,dash:'solid'}},");

            // Balance ratio zone 0.8-1.2
            sb.AppendLine("    {type:'rect', xref:'paper', x0:0, x1:1, yref:'y5', y0:0.8, y1:1.2, fillcolor:'rgba(0,255,0,0.1)', line:{width:0}}");
        }

        /// <summary>
        /// Builds the chart title string.
        /// </summary>
        private static string BuildTitle(string stockId, string companyName)
        {
            if (!string.IsNullOrEmpty(stockId) && !string.IsNullOrEmpty(companyName))
                return $"{stockId} {companyName} - Day High Breakout + Momentum Exhaustion Exit Strategy";
            if (!string.IsNullOrEmpty(stockId))
                return $"{stockId} - Day High Breakout + Momentum Exhaustion Exit Strategy";
            return "Day High Breakout + Momentum Exhaustion Exit Strategy Analysis";
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
