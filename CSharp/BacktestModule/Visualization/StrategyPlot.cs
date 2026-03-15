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
using BacktestModule.Strategy;
using BacktestModule.Core.Models;
using Microsoft.Extensions.Logging;
using BacktestModule.Core;

namespace BacktestModule.Visualization
{
    /// <summary>
    /// Alternative strategy visualization module with 3-subplot layout:
    /// 1. Price + Day High + entry/exit markers + reference lines
    /// 2. Ratio (15s_300s and 15s_180s_w321)
    /// 3. Price change percentage
    ///
    /// Generates interactive HTML with embedded Plotly.js and a trade summary table.
    /// </summary>
    public static class StrategyPlot
    {
        /// <summary>
        /// Gets the tick size based on Taiwan Stock Exchange rules.
        /// </summary>
        /// <param name="price">Stock price.</param>
        /// <returns>Tick size for the given price level.</returns>
        public static double GetTick(double price)
        {
            if (price < 10) return 0.01;
            if (price < 50) return 0.05;
            if (price < 100) return 0.1;
            if (price < 500) return 0.5;
            if (price < 1000) return 1.0;
            return 5.0;
        }

        /// <summary>
        /// Calculates the limit-up price (rounded down to tick multiple).
        /// </summary>
        /// <param name="refPrice">Reference (previous close) price.</param>
        /// <param name="factor">Price factor (default 1.1 for 10% limit).</param>
        /// <returns>Limit-up price.</returns>
        public static double CalcLimitUpPrice(double refPrice, double factor = 1.1)
        {
            if (refPrice > 0)
            {
                double limitUp = refPrice * factor;
                double tick = GetTick(limitUp);
                double adjusted = Math.Floor(limitUp / tick) * tick;
                return Math.Round(adjusted, 2);
            }
            return refPrice;
        }

        /// <summary>
        /// Creates an interactive HTML file containing the strategy visualization chart
        /// with a trade summary table.
        /// </summary>
        /// <param name="stockId">Stock code.</param>
        /// <param name="sectorName">Sector/group name.</param>
        /// <param name="priceData">Price data: list of (DateTime, double price) tuples.</param>
        /// <param name="events">Event list (entry/partial_exit/supplement/exit).</param>
        /// <param name="refPrice">Previous close price.</param>
        /// <param name="outputPath">HTML output file path.</param>
        /// <param name="ratioData">Ratio data: list of (DateTime, double ratio_15s_300s, double? ratio_15s_180s_w321) tuples. Can be null.</param>
        /// <param name="dayHighData">Day High data: list of (DateTime, double dayHigh) tuples. Can be null.</param>
        /// <param name="exhaustionInfo">Exhaustion info list. Can be null.</param>
        /// <param name="logger">Logger instance. Can be null.</param>
        public static void CreateStrategyHtml(
            string stockId,
            string sectorName,
            List<PricePoint> priceData,
            List<StrategyEvent> events,
            double refPrice,
            string outputPath,
            List<RatioPoint> ratioData = null,
            List<DayHighPoint> dayHighData = null,
            List<ExhaustionInfo> exhaustionInfo = null,
            ILogger logger = null)
        {
            double limitUpPrice = CalcLimitUpPrice(refPrice);
            bool hasRatio = ratioData != null && ratioData.Count > 0;

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head>");
            sb.AppendLine("<meta charset='utf-8'>");
            sb.AppendLine($"<title>{stockId} [{sectorName}]</title>");
            sb.AppendLine("<script src='https://cdn.plot.ly/plotly-latest.min.js'></script>");
            sb.AppendLine("</head><body>");
            sb.AppendLine("<div id='chart' style='width:100%;height:900px;'></div>");
            sb.AppendLine("<script>");

            sb.AppendLine("var traces = [];");

            // === Subplot 1: Price ===
            BuildPriceSubplot(sb, priceData, dayHighData, events, refPrice, limitUpPrice);

            // === Subplot 2: Ratio ===
            BuildRatioSubplot(sb, ratioData, events, exhaustionInfo, hasRatio);

            // === Subplot 3: Change % ===
            BuildChangePctSubplot(sb, priceData, refPrice);

            // === Layout ===
            sb.AppendLine("var layout = {");
            sb.AppendLine($"  title: {{text:'<b>{Sanitize(stockId)}</b> [{Sanitize(sectorName)}]', font:{{size:18}}}},");
            sb.AppendLine("  height: 900,");
            sb.AppendLine("  showlegend: true,");
            sb.AppendLine("  legend: {orientation:'h', yanchor:'bottom', y:1.02, xanchor:'right', x:1},");
            sb.AppendLine("  hovermode: 'x unified',");
            sb.AppendLine("  template: 'plotly_white',");

            double priceMin = priceData.Min(p => p.Price);
            double priceMax = priceData.Max(p => p.Price);
            double priceMargin = (priceMax - priceMin) * 0.1;

            sb.AppendLine("  xaxis:  {showticklabels:false, domain:[0,1], anchor:'y'},");
            sb.AppendLine($"  yaxis:  {{title:'Price', domain:[0.55,1.0], range:[{Fmt(priceMin - priceMargin)},{Fmt(priceMax + priceMargin)}]}},");
            sb.AppendLine("  xaxis2: {showticklabels:false, domain:[0,1], anchor:'y2'},");
            sb.AppendLine("  yaxis2: {title:'Ratio', domain:[0.28,0.50]},");
            sb.AppendLine("  xaxis3: {tickformat:'%H:%M:%S', title:'Time', domain:[0,1], anchor:'y3'},");
            sb.AppendLine("  yaxis3: {title:'Change %', domain:[0.0,0.22]},");

            // Shapes
            sb.AppendLine("  shapes: [");
            // Ref price
            if (refPrice > 0)
                sb.AppendLine($"    {{type:'line', xref:'paper', x0:0, x1:1, yref:'y', y0:{Fmt(refPrice)}, y1:{Fmt(refPrice)}, line:{{color:'gray',dash:'dash'}}}},");
            // Limit up
            sb.AppendLine($"    {{type:'line', xref:'paper', x0:0, x1:1, yref:'y', y0:{Fmt(limitUpPrice)}, y1:{Fmt(limitUpPrice)}, line:{{color:'red',dash:'dash'}}}},");
            // Ratio 1.0
            sb.AppendLine("    {type:'line', xref:'paper', x0:0, x1:1, yref:'y2', y0:1.0, y1:1.0, line:{color:'gray',dash:'dash'}},");
            // Ratio 5.5 threshold
            sb.AppendLine("    {type:'line', xref:'paper', x0:0, x1:1, yref:'y2', y0:5.5, y1:5.5, line:{color:'orange',dash:'dot'}},");
            // Change % zero
            sb.AppendLine("    {type:'line', xref:'paper', x0:0, x1:1, yref:'y3', y0:0, y1:0, line:{color:'gray',dash:'dash'}},");
            // Change % 10% limit
            sb.AppendLine("    {type:'line', xref:'paper', x0:0, x1:1, yref:'y3', y0:10.0, y1:10.0, line:{color:'red',dash:'dot'}}");
            sb.AppendLine("  ]");
            sb.AppendLine("};");

            sb.AppendLine("Plotly.newPlot('chart', traces, layout, {displayModeBar:true, scrollZoom:true});");
            sb.AppendLine("</script>");

            // Trade summary table
            string summaryHtml = GenerateTradeSummary(events, refPrice, limitUpPrice, exhaustionInfo);
            sb.AppendLine(summaryHtml);

            sb.AppendLine("</body></html>");

            // Ensure output directory exists
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            logger?.LogInformation("HTML saved: {OutputPath}", outputPath);
        }

        /// <summary>
        /// Builds price subplot traces.
        /// </summary>
        private static void BuildPriceSubplot(
            StringBuilder sb,
            List<PricePoint> priceData,
            List<DayHighPoint> dayHighData,
            List<StrategyEvent> events,
            double refPrice, double limitUpPrice)
        {
            var times = priceData.Select(p => $"'{FmtTime(p.Time)}'").ToList();
            var prices = priceData.Select(p => Fmt(p.Price)).ToList();

            // Price line (black)
            sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(prices)}], mode:'lines', name:'Price', line:{{color:'black',width:1}}, xaxis:'x', yaxis:'y'}});");

            // Day High line
            if (dayHighData != null && dayHighData.Count > 0)
            {
                var dhTimes = dayHighData.Select(d => $"'{FmtTime(d.Time)}'").ToList();
                var dhValues = dayHighData.Select(d => Fmt(d.DayHigh)).ToList();
                sb.AppendLine($"traces.push({{x:[{J(dhTimes)}], y:[{J(dhValues)}], mode:'lines', name:'Day High', line:{{color:'#FF9800',width:2,dash:'dot'}}, xaxis:'x', yaxis:'y'}});");
            }

            // Entry markers (red circle)
            var entryEvents = events.Where(e => e.Type == "entry").ToList();
            if (entryEvents.Count > 0)
            {
                var eTimes = entryEvents.Select(e => $"'{FmtTime(e.Time)}'").ToList();
                var ePrices = entryEvents.Select(e => Fmt(e.Price)).ToList();
                sb.AppendLine($"traces.push({{x:[{J(eTimes)}], y:[{J(ePrices)}], mode:'markers', name:'Entry', marker:{{symbol:'circle',size:18,color:'red',line:{{color:'white',width:2}}}}, xaxis:'x', yaxis:'y'}});");
            }

            // Partial exit markers (orange triangle-down)
            var partialExits = events.Where(e => e.Type == "partial_exit").ToList();
            if (partialExits.Count > 0)
            {
                var pTimes = partialExits.Select(e => $"'{FmtTime(e.Time)}'").ToList();
                var pPrices = partialExits.Select(e => Fmt(e.Price)).ToList();
                sb.AppendLine($"traces.push({{x:[{J(pTimes)}], y:[{J(pPrices)}], mode:'markers', name:'Partial Exit (50%)', marker:{{symbol:'triangle-down',size:16,color:'#FF9800',line:{{color:'white',width:2}}}}, xaxis:'x', yaxis:'y'}});");
            }

            // Supplement markers (blue triangle-up)
            var supplements = events.Where(e => e.Type == "supplement").ToList();
            if (supplements.Count > 0)
            {
                var sTimes = supplements.Select(e => $"'{FmtTime(e.Time)}'").ToList();
                var sPrices = supplements.Select(e => Fmt(e.Price)).ToList();
                sb.AppendLine($"traces.push({{x:[{J(sTimes)}], y:[{J(sPrices)}], mode:'markers', name:'Supplement', marker:{{symbol:'triangle-up',size:16,color:'#2196F3',line:{{color:'white',width:2}}}}, xaxis:'x', yaxis:'y'}});");
            }

            // Exit markers (chartreuse circle)
            var exitEvents = events.Where(e => e.Type == "exit").ToList();
            if (exitEvents.Count > 0)
            {
                var xTimes = exitEvents.Select(e => $"'{FmtTime(e.Time)}'").ToList();
                var xPrices = exitEvents.Select(e => Fmt(e.Price)).ToList();
                sb.AppendLine($"traces.push({{x:[{J(xTimes)}], y:[{J(xPrices)}], mode:'markers', name:'Exit', marker:{{symbol:'circle',size:18,color:'#7FFF00',line:{{color:'white',width:2}}}}, xaxis:'x', yaxis:'y'}});");
            }
        }

        /// <summary>
        /// Builds ratio subplot traces.
        /// </summary>
        private static void BuildRatioSubplot(
            StringBuilder sb,
            List<RatioPoint> ratioData,
            List<StrategyEvent> events,
            List<ExhaustionInfo> exhaustionInfo,
            bool hasRatio)
        {
            if (!hasRatio) return;

            var rTimes = ratioData.Select(r => $"'{FmtTime(r.Time)}'").ToList();

            // ratio_15s_300s line (blue)
            if (ratioData.Any(r => r.Ratio15s300s.HasValue))
            {
                var vals = ratioData.Select(r => r.Ratio15s300s.HasValue ? Fmt(r.Ratio15s300s.Value) : "null").ToList();
                sb.AppendLine($"traces.push({{x:[{J(rTimes)}], y:[{J(vals)}], mode:'lines', name:'15s_300s', line:{{color:'#2196F3',width:1.5}}, xaxis:'x2', yaxis:'y2'}});");
            }

            // ratio_15s_180s_w321 line (purple dotted)
            if (ratioData.Any(r => r.Ratio15s180sW321.HasValue))
            {
                var vals = ratioData.Select(r => r.Ratio15s180sW321.HasValue ? Fmt(r.Ratio15s180sW321.Value) : "null").ToList();
                sb.AppendLine($"traces.push({{x:[{J(rTimes)}], y:[{J(vals)}], mode:'lines', name:'15s_180s_w321', line:{{color:'#9C27B0',width:1,dash:'dot'}}, xaxis:'x2', yaxis:'y2'}});");
            }

            // Fallback: single ratio column
            if (ratioData.Any(r => r.Ratio.HasValue) && !ratioData.Any(r => r.Ratio15s300s.HasValue))
            {
                var vals = ratioData.Select(r => r.Ratio.HasValue ? Fmt(r.Ratio.Value) : "null").ToList();
                sb.AppendLine($"traces.push({{x:[{J(rTimes)}], y:[{J(vals)}], mode:'lines', name:'Ratio', line:{{color:'#9C27B0',width:1}}, xaxis:'x2', yaxis:'y2'}});");
            }

            // Entry markers on ratio chart
            var entryEvents = events.Where(e => e.Type == "entry").ToList();
            if (entryEvents.Count > 0 && ratioData.Count > 0)
            {
                var entryRatioTimes = new List<string>();
                var entryRatioVals = new List<string>();

                foreach (var e in entryEvents)
                {
                    // Find closest ratio value
                    var closest = ratioData.OrderBy(r => Math.Abs((r.Time - e.Time).TotalMilliseconds)).First();
                    double ratioVal = closest.Ratio15s300s ?? closest.Ratio ?? 0;
                    entryRatioTimes.Add($"'{FmtTime(e.Time)}'");
                    entryRatioVals.Add(Fmt(ratioVal));
                }

                sb.AppendLine($"traces.push({{x:[{J(entryRatioTimes)}], y:[{J(entryRatioVals)}], mode:'markers', name:'Entry (Ratio)', marker:{{symbol:'circle',size:15,color:'red',line:{{color:'white',width:1}}}}, showlegend:false, xaxis:'x2', yaxis:'y2'}});");
            }

            // Exhaustion info markers
            if (exhaustionInfo != null)
            {
                foreach (var info in exhaustionInfo)
                {
                    if (info.PeakRatio > 0)
                    {
                        sb.AppendLine($"traces.push({{x:['{FmtTime(info.Time)}'], y:[{Fmt(info.PeakRatio)}], mode:'markers+text', name:'Peak Ratio', marker:{{symbol:'triangle-up',size:10,color:'#E91E63',line:{{color:'white',width:1}}}}, text:['Peak:{info.PeakRatio:F1}'], textposition:'top center', textfont:{{size:9,color:'#E91E63'}}, showlegend:false, xaxis:'x2', yaxis:'y2'}});");
                    }
                }
            }
        }

        /// <summary>
        /// Builds change percentage subplot traces.
        /// </summary>
        private static void BuildChangePctSubplot(StringBuilder sb, List<PricePoint> priceData, double refPrice)
        {
            if (refPrice <= 0) return;

            var times = priceData.Select(p => $"'{FmtTime(p.Time)}'").ToList();
            var changePcts = priceData.Select(p => Fmt((p.Price - refPrice) / refPrice * 100)).ToList();

            sb.AppendLine($"traces.push({{x:[{J(times)}], y:[{J(changePcts)}], mode:'lines', name:'Change %', line:{{color:'#00BCD4',width:1}}, xaxis:'x3', yaxis:'y3'}});");
        }

        /// <summary>
        /// Generates the HTML trade summary table to be appended after the chart.
        /// </summary>
        private static string GenerateTradeSummary(
            List<StrategyEvent> events, double refPrice, double limitUpPrice,
            List<ExhaustionInfo> exhaustionInfo)
        {
            if (events == null || events.Count == 0)
                return "";

            var entryEvents = events.Where(e => e.Type == "entry").ToList();
            var exitEvents = events.Where(e => e.Type == "exit").ToList();
            var partialExitEvents = events.Where(e => e.Type == "partial_exit").ToList();
            var supplementEvents = events.Where(e => e.Type == "supplement").ToList();

            double totalPnl = exitEvents.Sum(e => e.PnlPct);
            int wins = exitEvents.Count(e => e.PnlPct > 0);
            int losses = exitEvents.Count(e => e.PnlPct < 0);
            int breakeven = exitEvents.Count(e => Math.Abs(e.PnlPct) < 0.0001);
            int totalExits = wins + losses + breakeven;
            double winRate = totalExits > 0 ? (double)wins / totalExits * 100 : 0;

            var html = new StringBuilder();
            html.AppendLine("<div style='margin:20px;padding:20px;background:#f5f5f5;border-radius:8px;font-family:Arial,sans-serif;'>");
            html.AppendLine("<h3 style='margin-bottom:15px;'>Trade Summary</h3>");
            html.AppendLine("<table style='width:100%;border-collapse:collapse;'>");

            html.AppendLine($"<tr><td style='padding:8px;border-bottom:1px solid #ddd;'><b>Prev Close</b></td><td style='padding:8px;border-bottom:1px solid #ddd;'>{refPrice:F2}</td>");
            html.AppendLine($"<td style='padding:8px;border-bottom:1px solid #ddd;'><b>Limit Up</b></td><td style='padding:8px;border-bottom:1px solid #ddd;color:red;'>{limitUpPrice:F2}</td></tr>");

            html.AppendLine($"<tr><td style='padding:8px;border-bottom:1px solid #ddd;'><b>Entries</b></td><td style='padding:8px;border-bottom:1px solid #ddd;'>{entryEvents.Count}</td>");
            html.AppendLine($"<td style='padding:8px;border-bottom:1px solid #ddd;'><b>Full Exits</b></td><td style='padding:8px;border-bottom:1px solid #ddd;'>{exitEvents.Count}</td></tr>");

            html.AppendLine($"<tr><td style='padding:8px;border-bottom:1px solid #ddd;'><b>Partial Exits</b></td><td style='padding:8px;border-bottom:1px solid #ddd;color:#FF9800;'>{partialExitEvents.Count}</td>");
            html.AppendLine($"<td style='padding:8px;border-bottom:1px solid #ddd;'><b>Supplements</b></td><td style='padding:8px;border-bottom:1px solid #ddd;color:#2196F3;'>{supplementEvents.Count}</td></tr>");

            html.AppendLine($"<tr><td style='padding:8px;border-bottom:1px solid #ddd;'><b>Win Rate</b></td><td style='padding:8px;border-bottom:1px solid #ddd;'>{wins}/{totalExits} ({winRate:F0}%)</td>");
            html.AppendLine($"<td style='padding:8px;border-bottom:1px solid #ddd;'><b>W/E/L</b></td><td style='padding:8px;border-bottom:1px solid #ddd;'>{wins} / {breakeven} / {losses}</td></tr>");

            string pnlColor = totalPnl >= 0 ? "green" : "red";
            html.AppendLine($"<tr><td style='padding:8px;'><b>Total PnL</b></td><td style='padding:8px;color:{pnlColor};font-weight:bold;' colspan='3'>{totalPnl:+0.00;-0.00}%</td></tr>");
            html.AppendLine("</table>");

            // Trade details table
            if (exitEvents.Count > 0)
            {
                html.AppendLine("<h4 style='margin-top:20px;margin-bottom:10px;'>Trade Details</h4>");
                html.AppendLine("<table style='width:100%;border-collapse:collapse;font-size:14px;'>");
                html.AppendLine("<tr style='background:#e0e0e0;'>");
                html.AppendLine("<th style='padding:8px;text-align:left;'>Entry Time</th>");
                html.AppendLine("<th style='padding:8px;text-align:left;'>Exit Time</th>");
                html.AppendLine("<th style='padding:8px;text-align:right;'>Entry Price</th>");
                html.AppendLine("<th style='padding:8px;text-align:right;'>Exit Price</th>");
                html.AppendLine("<th style='padding:8px;text-align:left;'>Exit Reason</th>");
                html.AppendLine("<th style='padding:8px;text-align:right;'>Peak Ratio</th>");
                html.AppendLine("<th style='padding:8px;text-align:right;'>Drawdown</th>");
                html.AppendLine("<th style='padding:8px;text-align:right;'>PnL</th>");
                html.AppendLine("</tr>");

                for (int i = 0; i < exitEvents.Count; i++)
                {
                    var exitE = exitEvents[i];
                    var entryE = i < entryEvents.Count ? entryEvents[i] : null;
                    double pnl = exitE.PnlPct;
                    string pnlClr = pnl > 0 ? "green" : (pnl < 0 ? "red" : "gray");

                    string entryTime = entryE != null ? FmtTimeShort(entryE.Time) : "";
                    string exitTime = FmtTimeShort(exitE.Time);

                    var reasonMap = new Dictionary<string, string>
                    {
                        ["price_stop_loss"] = "Price Stop Loss",
                        ["momentum_stop_loss"] = "Momentum Stop Loss",
                        ["exhaustion"] = "Exhaustion",
                        ["end_of_day"] = "Market Close"
                    };
                    string reason = reasonMap.ContainsKey(exitE.ExitReason ?? "")
                        ? reasonMap[exitE.ExitReason] : (exitE.ExitReason ?? "");

                    string peakRatio = "-";
                    string drawdown = "-";
                    if (exhaustionInfo != null && i < exhaustionInfo.Count)
                    {
                        var info = exhaustionInfo[i];
                        if (info.PeakRatio > 0) peakRatio = $"{info.PeakRatio:F2}";
                        if (info.Drawdown > 0) drawdown = $"{info.Drawdown * 100:F1}%";
                    }

                    html.AppendLine("<tr style='border-bottom:1px solid #ddd;'>");
                    html.AppendLine($"<td style='padding:8px;'>{entryTime}</td>");
                    html.AppendLine($"<td style='padding:8px;'>{exitTime}</td>");
                    html.AppendLine($"<td style='padding:8px;text-align:right;'>{(entryE?.Price ?? 0):F2}</td>");
                    html.AppendLine($"<td style='padding:8px;text-align:right;'>{exitE.Price:F2}</td>");
                    html.AppendLine($"<td style='padding:8px;'>{reason}</td>");
                    html.AppendLine($"<td style='padding:8px;text-align:right;color:#9C27B0;'>{peakRatio}</td>");
                    html.AppendLine($"<td style='padding:8px;text-align:right;color:#E91E63;'>{drawdown}</td>");
                    html.AppendLine($"<td style='padding:8px;text-align:right;color:{pnlClr};font-weight:bold;'>{pnl:+0.00;-0.00}%</td>");
                    html.AppendLine("</tr>");
                }
                html.AppendLine("</table>");
            }

            // Partial exit details
            if (partialExitEvents.Count > 0)
            {
                html.AppendLine("<h4 style='margin-top:20px;margin-bottom:10px;'>Partial Exit Details</h4>");
                html.AppendLine("<table style='width:100%;border-collapse:collapse;font-size:14px;'>");
                html.AppendLine("<tr style='background:#FFF3E0;'>");
                html.AppendLine("<th style='padding:8px;text-align:left;'>Time</th>");
                html.AppendLine("<th style='padding:8px;text-align:right;'>Price</th>");
                html.AppendLine("<th style='padding:8px;text-align:left;'>Reason</th>");
                html.AppendLine("<th style='padding:8px;text-align:right;'>Quantity</th>");
                html.AppendLine("<th style='padding:8px;text-align:right;'>PnL</th>");
                html.AppendLine("</tr>");

                foreach (var e in partialExitEvents)
                {
                    double pnl = e.PnlPct;
                    string pnlClr = pnl > 0 ? "green" : (pnl < 0 ? "red" : "gray");
                    string eventTime = FmtTimeShort(e.Time);

                    html.AppendLine($"<tr style='border-bottom:1px solid #ddd;'>");
                    html.AppendLine($"<td style='padding:8px;'>{eventTime}</td>");
                    html.AppendLine($"<td style='padding:8px;text-align:right;'>{e.Price:F2}</td>");
                    html.AppendLine($"<td style='padding:8px;'>{e.Reason ?? ""}</td>");
                    html.AppendLine($"<td style='padding:8px;text-align:right;'>{e.Quantity} lots (50%)</td>");
                    html.AppendLine($"<td style='padding:8px;text-align:right;color:{pnlClr};font-weight:bold;'>{pnl:+0.00;-0.00}%</td>");
                    html.AppendLine("</tr>");
                }
                html.AppendLine("</table>");
            }

            // Supplement details
            if (supplementEvents.Count > 0)
            {
                html.AppendLine("<h4 style='margin-top:20px;margin-bottom:10px;'>Supplement Details</h4>");
                html.AppendLine("<table style='width:100%;border-collapse:collapse;font-size:14px;'>");
                html.AppendLine("<tr style='background:#E3F2FD;'>");
                html.AppendLine("<th style='padding:8px;text-align:left;'>Time</th>");
                html.AppendLine("<th style='padding:8px;text-align:right;'>Price</th>");
                html.AppendLine("<th style='padding:8px;text-align:left;'>Reason</th>");
                html.AppendLine("<th style='padding:8px;text-align:right;'>Quantity</th>");
                html.AppendLine("</tr>");

                foreach (var e in supplementEvents)
                {
                    string eventTime = FmtTimeShort(e.Time);
                    html.AppendLine($"<tr style='border-bottom:1px solid #ddd;'>");
                    html.AppendLine($"<td style='padding:8px;'>{eventTime}</td>");
                    html.AppendLine($"<td style='padding:8px;text-align:right;'>{e.Price:F2}</td>");
                    html.AppendLine($"<td style='padding:8px;'>{e.Reason ?? ""}</td>");
                    html.AppendLine($"<td style='padding:8px;text-align:right;'>{e.Quantity} lots</td>");
                    html.AppendLine("</tr>");
                }
                html.AppendLine("</table>");
            }

            html.AppendLine("</div>");
            return html.ToString();
        }

        // --- Helper methods ---

        private static string FmtTime(DateTime time)
            => time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

        private static string FmtTimeShort(DateTime time)
            => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        private static string Fmt(double value)
            => value.ToString("G", CultureInfo.InvariantCulture);

        private static string J(List<string> items)
            => string.Join(",", items);

        private static string Sanitize(string s)
            => s?.Replace("'", "\\'").Replace("<", "&lt;").Replace(">", "&gt;") ?? "";
    }

    // === Data models for StrategyPlot ===

    /// <summary>
    /// Represents a single price data point for the strategy plot.
    /// </summary>
    public class PricePoint
    {
        public DateTime Time { get; set; }
        public double Price { get; set; }
    }

    /// <summary>
    /// Represents a single Day High data point.
    /// </summary>
    public class DayHighPoint
    {
        public DateTime Time { get; set; }
        public double DayHigh { get; set; }
    }

    /// <summary>
    /// Represents a single ratio data point with multiple possible ratio columns.
    /// </summary>
    public class RatioPoint
    {
        public DateTime Time { get; set; }
        public double? Ratio { get; set; }
        public double? Ratio15s300s { get; set; }
        public double? Ratio15s180sW321 { get; set; }
    }

    /// <summary>
    /// Represents a strategy event (entry, partial_exit, supplement, exit).
    /// </summary>
    public class StrategyEvent
    {
        /// <summary>
        /// Event type: "entry", "partial_exit", "supplement", "exit"
        /// </summary>
        public string Type { get; set; }

        public DateTime Time { get; set; }
        public double Price { get; set; }

        /// <summary>
        /// Entry type description (for entry events).
        /// </summary>
        public string EntryType { get; set; }

        /// <summary>
        /// PnL percentage (for exit and partial_exit events).
        /// </summary>
        public double PnlPct { get; set; }

        /// <summary>
        /// Reason string (for exit/partial_exit events).
        /// </summary>
        public string Reason { get; set; }

        /// <summary>
        /// Exit reason code (for exit events): "price_stop_loss", "momentum_stop_loss", "exhaustion", "end_of_day".
        /// </summary>
        public string ExitReason { get; set; }

        /// <summary>
        /// Quantity in lots (for partial_exit and supplement events).
        /// </summary>
        public int Quantity { get; set; }
    }

    /// <summary>
    /// Represents exhaustion exit information for the ratio chart annotation.
    /// </summary>
    public class ExhaustionInfo
    {
        public DateTime Time { get; set; }
        public double PeakRatio { get; set; }
        public double CurrentRatio { get; set; }
        public double Drawdown { get; set; }
        public int ResetCount { get; set; }
    }
}
