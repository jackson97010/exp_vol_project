using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;

namespace StrongestVwap.Visualization
{
    /// <summary>
    /// Standalone trade chart generator.
    /// Reads tick data from per-stock parquet, overlays trade info, outputs interactive HTML.
    /// </summary>
    public static class TradeChartGenerator
    {
        public class TradeInfo
        {
            public string StockId { get; set; } = "";
            public string Date { get; set; } = "";
            public DateTime EntryTime { get; set; }
            public double EntryPrice { get; set; }
            public double EntryVwap { get; set; }
            public double EntryDayHigh { get; set; }
            public double TotalShares { get; set; }
            public double PositionCash { get; set; }
            public string GroupName { get; set; } = "";
            public int GroupRank { get; set; }
            public int MemberRank { get; set; }
            public DateTime ExitTime { get; set; }
            public double ExitPrice { get; set; }
            public string ExitReason { get; set; } = "";
            public double PnlAmount { get; set; }
            public double PnlPercent { get; set; }
            public double StopLossPrice { get; set; }
            public List<(string Label, double Price, DateTime? FillTime)> TpOrders { get; set; } = new();
        }

        /// <summary>
        /// Generate a chart for a single trade.
        /// </summary>
        public static string Generate(
            string tickParquetPath,
            TradeInfo trade,
            string outputHtmlPath,
            string pngOutputPath = null,
            TimeSpan? windowBefore = null,
            TimeSpan? windowAfter = null)
        {
            // Load tick data
            var (times, prices, volumes, vwaps, dayHighs) = LoadTickData(tickParquetPath);
            if (times.Count == 0)
            {
                Console.WriteLine("[CHART] No tick data loaded.");
                return null;
            }

            Console.WriteLine($"[CHART] Loaded {times.Count} trade ticks for {trade.StockId}");

            // Determine time window
            var before = windowBefore ?? TimeSpan.FromMinutes(10);
            var after = windowAfter ?? TimeSpan.FromMinutes(15);
            DateTime winStart = trade.EntryTime - before;
            DateTime winEnd = trade.ExitTime + after;

            // Filter to window
            var wIdx = new List<int>();
            for (int i = 0; i < times.Count; i++)
            {
                if (times[i] >= winStart && times[i] <= winEnd)
                    wIdx.Add(i);
            }

            if (wIdx.Count == 0)
            {
                Console.WriteLine("[CHART] No ticks in window, using all data.");
                wIdx = Enumerable.Range(0, times.Count).ToList();
            }

            // Downsample to ~1s for cleaner chart
            var sampled = Downsample1s(wIdx, times, prices, volumes, vwaps, dayHighs);
            Console.WriteLine($"[CHART] Sampled to {sampled.Times.Count} points");

            double firstPrice = prices[0];

            // Build HTML
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            sb.AppendLine($"<title>{trade.StockId} Trade Chart {trade.Date}</title>");
            sb.AppendLine("<script src='https://cdn.plot.ly/plotly-latest.min.js'></script>");
            sb.AppendLine("</head><body>");
            sb.AppendLine("<div id='chart' style='width:100%;height:100vh;'></div>");
            sb.AppendLine("<script>");

            sb.AppendLine("var traces = [];");

            // === Subplot 1: Price + VWAP + DayHigh + markers (y) ===
            EmitTrace(sb, sampled.Times, sampled.Prices, "Price", "black", 1.5, "x", "y");
            EmitTrace(sb, sampled.Times, sampled.Vwaps, "VWAP", "#2196F3", 1.5, "x", "y", "dash");
            EmitTrace(sb, sampled.Times, sampled.DayHighs, "Day High", "#FF9800", 2, "x", "y", "dot");

            // Entry marker
            EmitMarker(sb, trade.EntryTime, trade.EntryPrice,
                $"BUY {trade.EntryPrice:F1}", "red", "triangle-up", 15, "x", "y",
                $"Entry<br>Price: {F(trade.EntryPrice)}<br>VWAP: {F(trade.EntryVwap)}<br>DH: {F(trade.EntryDayHigh)}<br>Time: %{{x}}",
                "Entry");

            // Exit marker
            string exitColor = trade.PnlPercent >= 0 ? "green" : "red";
            EmitMarker(sb, trade.ExitTime, trade.ExitPrice,
                $"SELL {trade.ExitPrice:F1}", exitColor, "triangle-down", 15, "x", "y",
                $"Exit ({trade.ExitReason})<br>Price: {F(trade.ExitPrice)}<br>PnL: {trade.PnlPercent:F2}%<br>Time: %{{x}}",
                $"Exit ({trade.ExitReason})");

            // TP fill markers
            foreach (var tp in trade.TpOrders)
            {
                if (tp.FillTime.HasValue)
                {
                    EmitMarker(sb, tp.FillTime.Value, tp.Price,
                        tp.Label, "green", "star", 12, "x", "y",
                        $"{tp.Label}<br>Price: {F(tp.Price)}<br>Time: %{{x}}",
                        tp.Label);
                }
            }

            // Entry-Exit connection line
            sb.AppendLine($"traces.push({{x:['{FT(trade.EntryTime)}','{FT(trade.ExitTime)}'], y:[{F(trade.EntryPrice)},{F(trade.ExitPrice)}], mode:'lines', line:{{color:'{exitColor}',width:1.5,dash:'dot'}}, opacity:0.5, showlegend:false, hoverinfo:'skip', xaxis:'x', yaxis:'y'}});");

            // === Subplot 2: Volume (y2) ===
            // Color bars by price direction
            var volColors = new List<string>();
            for (int i = 0; i < sampled.Prices.Count; i++)
            {
                if (i == 0 || sampled.Prices[i] >= sampled.Prices[i - 1])
                    volColors.Add("rgba(0,200,0,0.5)");
                else
                    volColors.Add("rgba(200,0,0,0.5)");
            }
            var tStr = sampled.Times.Select(t => $"'{FT(t)}'").ToList();
            var vStr = sampled.Volumes.Select(v => F(v)).ToList();
            var cStr = volColors.Select(c => $"'{c}'").ToList();
            sb.AppendLine($"traces.push({{x:[{J(tStr)}], y:[{J(vStr)}], type:'bar', name:'Volume', marker:{{color:[{J(cStr)}]}}, xaxis:'x2', yaxis:'y2', showlegend:true}});");

            // === Subplot 3: Change % from first price (y3) ===
            var changePcts = sampled.Prices.Select(p => F((p - firstPrice) / firstPrice * 100)).ToList();
            sb.AppendLine($"traces.push({{x:[{J(tStr)}], y:[{J(changePcts)}], mode:'lines', name:'Change %', line:{{color:'steelblue',width:1.5}}, fill:'tozeroy', fillcolor:'rgba(70,130,180,0.1)', xaxis:'x3', yaxis:'y3'}});");

            // === Layout ===
            // Price range
            double pMin = sampled.Prices.Min();
            double pMax = sampled.Prices.Max();
            if (sampled.DayHighs.Count > 0) pMax = Math.Max(pMax, sampled.DayHighs.Max());
            double margin = (pMax - pMin) * 0.08;

            string title = $"{trade.StockId} Trade Chart - {trade.Date}";
            string subtitle = $"Entry: {trade.EntryTime:HH:mm:ss} @ {trade.EntryPrice:F1} | " +
                $"Exit: {trade.ExitTime:HH:mm:ss} @ {trade.ExitPrice:F1} ({trade.ExitReason}) | " +
                $"PnL: {trade.PnlPercent:F2}%";

            sb.AppendLine("var layout = {");
            sb.AppendLine($"  title: {{text:\"{Esc(title)}<br><sup>{Esc(subtitle)}</sup>\", font:{{size:16}}}},");
            sb.AppendLine("  showlegend:true,");
            sb.AppendLine("  legend:{orientation:'h',yanchor:'bottom',y:1.02,xanchor:'right',x:1},");
            sb.AppendLine("  height:950,");
            sb.AppendLine("  hovermode:'x unified',");
            sb.AppendLine("  template:'plotly_white',");

            // Axes: Price 55%, Volume 18%, Change% 22%
            sb.AppendLine("  xaxis:  {showticklabels:false, showgrid:true, gridcolor:'lightgray', anchor:'y'},");
            sb.AppendLine($"  yaxis:  {{title:'Price', domain:[0.48,1.0], range:[{F(pMin - margin)},{F(pMax + margin)}]}},");
            sb.AppendLine("  xaxis2: {showticklabels:false, showgrid:true, gridcolor:'lightgray', anchor:'y2', matches:'x'},");
            sb.AppendLine("  yaxis2: {title:'Volume', domain:[0.26,0.44]},");
            sb.AppendLine("  xaxis3: {tickformat:'%H:%M:%S', title:'Time', showgrid:true, gridcolor:'lightgray', anchor:'y3', matches:'x'},");
            sb.AppendLine("  yaxis3: {title:'Change %', domain:[0.0,0.22]},");

            // Shapes: stop loss line, TP levels, holding period
            sb.AppendLine("  shapes:[");

            // Stop loss line
            if (trade.StopLossPrice > 0)
                sb.AppendLine($"    {{type:'line',xref:'paper',x0:0,x1:1,yref:'y',y0:{F(trade.StopLossPrice)},y1:{F(trade.StopLossPrice)},line:{{color:'orange',dash:'dash',width:1.5}}}},");

            // TP level lines
            foreach (var tp in trade.TpOrders)
            {
                sb.AppendLine($"    {{type:'line',xref:'paper',x0:0,x1:1,yref:'y',y0:{F(tp.Price)},y1:{F(tp.Price)},line:{{color:'green',dash:'dot',width:1}}}},");
            }

            // Change % = 0 line
            sb.AppendLine("    {type:'line',xref:'paper',x0:0,x1:1,yref:'y3',y0:0,y1:0,line:{color:'gray',dash:'dash'}}");

            sb.AppendLine("  ],");

            // Annotations: stop loss label, TP labels, trade info box
            sb.AppendLine("  annotations:[");

            if (trade.StopLossPrice > 0)
            {
                sb.AppendLine($"    {{xref:'paper',x:1.0,yref:'y',y:{F(trade.StopLossPrice)},text:'SL {F(trade.StopLossPrice)}',showarrow:false,font:{{size:9,color:'orange'}},xanchor:'left'}},");
            }

            foreach (var tp in trade.TpOrders)
            {
                sb.AppendLine($"    {{xref:'paper',x:1.0,yref:'y',y:{F(tp.Price)},text:'{Esc(tp.Label)} {F(tp.Price)}',showarrow:false,font:{{size:9,color:'green'}},xanchor:'left'}},");
            }

            // Trade info box
            string infoText = $"Stock: {trade.StockId}<br>" +
                $"Group: {Esc(trade.GroupName)} (R{trade.GroupRank}/M{trade.MemberRank})<br>" +
                $"Entry: {trade.EntryTime:HH:mm:ss} @ {trade.EntryPrice:F2}<br>" +
                $"VWAP: {trade.EntryVwap:F2} | DH: {trade.EntryDayHigh:F2}<br>" +
                $"SL: {trade.StopLossPrice:F2}<br>" +
                $"Shares: {trade.TotalShares:F0} ({trade.PositionCash / 1e6:F0}M)<br>" +
                $"Exit: {trade.ExitTime:HH:mm:ss} @ {trade.ExitPrice:F2} ({trade.ExitReason})<br>" +
                $"PnL: {trade.PnlAmount:F0} ({trade.PnlPercent:F2}%)";

            // TP fill info
            var tpFills = trade.TpOrders.Where(t => t.FillTime.HasValue).ToList();
            if (tpFills.Count > 0)
            {
                infoText += "<br>TP fills: " + string.Join(", ", tpFills.Select(t => $"{t.Label}@{t.Price:F1}"));
            }

            sb.AppendLine($"    {{xref:'paper',yref:'paper',x:0.01,y:0.98,text:'{Esc(infoText)}',showarrow:false,font:{{size:10}},align:'left',bgcolor:'rgba(255,255,255,0.92)',bordercolor:'black',borderwidth:1}}");

            sb.AppendLine("  ]");
            sb.AppendLine("};");
            sb.AppendLine("Plotly.newPlot('chart', traces, layout, {displayModeBar:true, scrollZoom:true});");

            sb.AppendLine("</script></body></html>");

            // Save
            string dir = Path.GetDirectoryName(outputHtmlPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outputHtmlPath, sb.ToString(), Encoding.UTF8);
            Console.WriteLine($"[CHART] HTML saved: {outputHtmlPath}");

            // PNG via Playwright
            if (!string.IsNullOrEmpty(pngOutputPath))
                GeneratePng(outputHtmlPath, pngOutputPath);

            return outputHtmlPath;
        }

        #region Data loading

        private static (List<DateTime> Times, List<double> Prices, List<double> Volumes,
            List<double> Vwaps, List<double> DayHighs) LoadTickData(string path)
        {
            var times = new List<DateTime>();
            var prices = new List<double>();
            var volumes = new List<double>();
            var vwaps = new List<double>();
            var dayHighs = new List<double>();

            if (!File.Exists(path))
            {
                Console.WriteLine($"[CHART] File not found: {path}");
                return (times, prices, volumes, vwaps, dayHighs);
            }

            try
            {
                Task.Run(async () =>
                {
                    using var stream = File.OpenRead(path);
                    using var reader = await ParquetReader.CreateAsync(stream);
                    var fields = reader.Schema.GetDataFields();
                    var fieldNames = fields.Select(f => f.Name).ToList();

                    int typeIdx = fieldNames.IndexOf("type");
                    int timeIdx = fieldNames.IndexOf("time");
                    int priceIdx = fieldNames.IndexOf("price");
                    int volIdx = fieldNames.IndexOf("volume");
                    int vwapIdx = fieldNames.IndexOf("vwap");
                    int dhIdx = fieldNames.IndexOf("day_high");

                    for (int rg = 0; rg < reader.RowGroupCount; rg++)
                    {
                        using var rgr = reader.OpenRowGroupReader(rg);
                        var cols = new DataColumn[fields.Length];
                        for (int c = 0; c < fields.Length; c++)
                            cols[c] = await rgr.ReadColumnAsync(fields[c]);

                        int rowCount = (int)rgr.RowCount;
                        for (int r = 0; r < rowCount; r++)
                        {
                            // Filter: only Trade ticks
                            if (typeIdx >= 0)
                            {
                                string type = GetStr(cols[typeIdx], r);
                                if (type != "Trade") continue;
                            }

                            double p = priceIdx >= 0 ? GetDbl(cols[priceIdx], r) : 0;
                            if (p <= 0) continue;

                            times.Add(timeIdx >= 0 ? GetDt(cols[timeIdx], r) : DateTime.MinValue);
                            prices.Add(p);
                            volumes.Add(volIdx >= 0 ? GetDbl(cols[volIdx], r) : 0);
                            vwaps.Add(vwapIdx >= 0 ? GetDbl(cols[vwapIdx], r) : 0);
                            dayHighs.Add(dhIdx >= 0 ? GetDbl(cols[dhIdx], r) : 0);
                        }
                    }
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CHART] Error loading: {ex.Message}");
            }

            return (times, prices, volumes, vwaps, dayHighs);
        }

        #endregion

        #region Downsample

        private record SampledData(List<DateTime> Times, List<double> Prices,
            List<double> Volumes, List<double> Vwaps, List<double> DayHighs);

        private static SampledData Downsample1s(List<int> indices,
            List<DateTime> times, List<double> prices, List<double> volumes,
            List<double> vwaps, List<double> dayHighs)
        {
            var grouped = new Dictionary<long, List<int>>();
            foreach (int i in indices)
            {
                long key = times[i].Ticks / TimeSpan.TicksPerSecond;
                if (!grouped.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    grouped[key] = list;
                }
                list.Add(i);
            }

            var st = new List<DateTime>();
            var sp = new List<double>();
            var sv = new List<double>();
            var sw = new List<double>();
            var sd = new List<double>();

            foreach (var kv in grouped.OrderBy(k => k.Key))
            {
                var idxs = kv.Value;
                int last = idxs[^1];
                st.Add(times[last]);
                sp.Add(prices[last]);
                sv.Add(idxs.Sum(i => volumes[i]));
                sw.Add(vwaps[last]);
                sd.Add(dayHighs[last]);
            }

            return new SampledData(st, sp, sv, sw, sd);
        }

        #endregion

        #region HTML helpers

        private static void EmitTrace(StringBuilder sb, List<DateTime> times, List<double> values,
            string name, string color, double width, string xaxis, string yaxis, string dash = null)
        {
            var tStr = times.Select(t => $"'{FT(t)}'").ToList();
            var vStr = values.Select(v => F(v)).ToList();
            string lineStr = dash != null
                ? $"{{color:'{color}',width:{F(width)},dash:'{dash}'}}"
                : $"{{color:'{color}',width:{F(width)}}}";
            sb.AppendLine($"traces.push({{x:[{J(tStr)}], y:[{J(vStr)}], mode:'lines', name:'{Esc(name)}', line:{lineStr}, xaxis:'{xaxis}', yaxis:'{yaxis}'}});");
        }

        private static void EmitMarker(StringBuilder sb, DateTime time, double price,
            string text, string color, string symbol, int size,
            string xaxis, string yaxis, string hovertemplate, string name)
        {
            sb.AppendLine($"traces.push({{x:['{FT(time)}'], y:[{F(price)}], mode:'markers+text', " +
                $"name:'{Esc(name)}', marker:{{color:'{color}',size:{size},symbol:'{symbol}',line:{{color:'white',width:1}}}}, " +
                $"text:['{Esc(text)}'], textposition:'bottom center', textfont:{{size:10,color:'{color}'}}, " +
                $"hovertemplate:'{Esc(hovertemplate)}', xaxis:'{xaxis}', yaxis:'{yaxis}'}});");
        }

        private static string FT(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        private static string F(double v) => v.ToString("G", CultureInfo.InvariantCulture);
        private static string J(List<string> items) => string.Join(",", items);
        private static string Esc(string s) => (s ?? "").Replace("'", "\\'").Replace("\"", "&quot;");

        #endregion

        #region Parquet column helpers

        private static DateTime GetDt(DataColumn col, int row)
        {
            var data = col.Data;
            if (data is DateTimeOffset[] dto) return dto[row].DateTime;
            if (data is DateTimeOffset?[] ndto && ndto[row].HasValue) return ndto[row]!.Value.DateTime;
            if (data is DateTime[] dt) return dt[row];
            if (data is long[] longs)
            {
                long val = longs[row];
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
                if (val > 1e16) return epoch.AddTicks(val / 100);
                if (val > 1e13) return epoch.AddTicks(val * 10);
                return epoch.AddMilliseconds(val);
            }
            if (data is long?[] nlongs && nlongs[row].HasValue)
            {
                long val = nlongs[row]!.Value;
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
                if (val > 1e16) return epoch.AddTicks(val / 100);
                if (val > 1e13) return epoch.AddTicks(val * 10);
                return epoch.AddMilliseconds(val);
            }
            return DateTime.MinValue;
        }

        private static double GetDbl(DataColumn col, int row)
        {
            var data = col.Data;
            if (data is double[] d) return d[row];
            if (data is float[] f) return f[row];
            if (data is long[] l) return l[row];
            if (data is int[] i) return i[row];
            if (data is double?[] nd && nd[row].HasValue) return nd[row]!.Value;
            return 0;
        }

        private static string GetStr(DataColumn col, int row)
        {
            var data = col.Data;
            if (data is string[] s) return s[row] ?? "";
            if (data is byte[][] b) return Encoding.UTF8.GetString(b[row] ?? Array.Empty<byte>());
            return data.GetValue(row)?.ToString() ?? "";
        }

        #endregion

        #region PNG generation

        private static void GeneratePng(string htmlPath, string pngPath)
        {
            try
            {
                string fullHtml = Path.GetFullPath(htmlPath);
                string fileUri = new Uri(fullHtml).AbsoluteUri;
                string fullPng = Path.GetFullPath(pngPath);

                string pngDir = Path.GetDirectoryName(fullPng);
                if (!string.IsNullOrEmpty(pngDir)) Directory.CreateDirectory(pngDir);

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c npx playwright screenshot --full-page \"{fileUri}\" \"{fullPng}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return;

                bool exited = process.WaitForExit(30_000);
                if (!exited) { process.Kill(); return; }

                if (process.ExitCode == 0 && File.Exists(fullPng))
                    Console.WriteLine($"[CHART] PNG saved: {fullPng}");
                else
                    Console.WriteLine($"[CHART] PNG generation failed: {process.StandardError.ReadToEnd()}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CHART] PNG error: {ex.Message}");
            }
        }

        #endregion
    }
}
