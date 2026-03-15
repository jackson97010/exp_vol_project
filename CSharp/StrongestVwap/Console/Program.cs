using System;
using System.Collections.Generic;
using System.IO;
using StrongestVwap.Core;
using StrongestVwap.Replay;
using StrongestVwap.Strategy;
using StrongestVwap.Visualization;

namespace StrongestVwap.App
{
    class Program
    {
        static int Main(string[] args)
        {
            System.Console.WriteLine("=== StrongestVwap Backtest System ===");
            System.Console.WriteLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            // Parse arguments
            string mode = "single";
            string date = "";
            string configPath = "configs/strongest_vwap.yaml";
            string? outputPath = null;
            string? groupCsvPath = null;
            string? tickDataPath = null;
            string? staticDataPath = null;
            string? screeningCsvPath = null;
            string? closeParquetPath = null;
            string? stockId = null;
            string? entryStartTime = null;
            bool bypassGroup = false;
            var dates = new List<string>();
            var stockList = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--stock_id":
                        if (i + 1 < args.Length) stockId = args[++i];
                        break;
                    case "--mode":
                        if (i + 1 < args.Length) mode = args[++i];
                        break;
                    case "--date":
                        if (i + 1 < args.Length) date = args[++i];
                        break;
                    case "--config":
                        if (i + 1 < args.Length) configPath = args[++i];
                        break;
                    case "--output_path":
                        if (i + 1 < args.Length) outputPath = args[++i];
                        break;
                    case "--group_csv":
                        if (i + 1 < args.Length) groupCsvPath = args[++i];
                        break;
                    case "--tick_data":
                        if (i + 1 < args.Length) tickDataPath = args[++i];
                        break;
                    case "--static_data":
                        if (i + 1 < args.Length) staticDataPath = args[++i];
                        break;
                    case "--screening_csv":
                        if (i + 1 < args.Length) screeningCsvPath = args[++i];
                        break;
                    case "--close_parquet":
                        if (i + 1 < args.Length) closeParquetPath = args[++i];
                        break;
                    case "--entry_start_time":
                        if (i + 1 < args.Length) entryStartTime = args[++i];
                        break;
                    case "--bypass_group":
                        bypassGroup = true;
                        break;
                    case "--stock_list":
                        while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                            stockList.Add(args[++i]);
                        break;
                    case "--dates":
                        // Read remaining args as dates
                        while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                            dates.Add(args[++i]);
                        break;
                    case "--help":
                        PrintUsage();
                        return 0;
                }
            }

            // Load config
            var config = new StrategyConfig(configPath);

            // Override config values from CLI
            if (outputPath != null)
                config.RawConfig["output_path"] = outputPath;
            if (groupCsvPath != null)
                config.RawConfig["group_csv_path"] = groupCsvPath;
            if (screeningCsvPath != null)
                config.RawConfig["screening_csv_path"] = screeningCsvPath;
            if (tickDataPath != null)
                config.RawConfig["tick_data_base_path"] = tickDataPath;

            if (entryStartTime != null && TimeSpan.TryParse(entryStartTime, out var parsedEntryStart))
                config.RawConfig["entry_start_time"] = parsedEntryStart;

            if (bypassGroup)
                config.RawConfig["bypass_group_screening"] = true;

            // Also check environment variable
            string? envOutput = Environment.GetEnvironmentVariable("STRONGEST_VWAP_OUTPUT_PATH");
            if (envOutput != null && outputPath == null)
                config.RawConfig["output_path"] = envOutput;

            var engine = new BacktestEngine(config);

            if (bypassGroup && stockList.Count > 0)
                engine.SetDirectStockList(stockList);

            switch (mode.ToLower())
            {
                case "single":
                    if (string.IsNullOrEmpty(date))
                    {
                        System.Console.WriteLine("[ERROR] --date is required for single mode.");
                        PrintUsage();
                        return 1;
                    }
                    engine.RunSingleDate(date, tickDataPath, staticDataPath);
                    break;

                case "batch":
                    if (dates.Count == 0 && !string.IsNullOrEmpty(date))
                        dates.Add(date);

                    if (dates.Count == 0)
                    {
                        System.Console.WriteLine("[ERROR] --date or --dates is required for batch mode.");
                        PrintUsage();
                        return 1;
                    }
                    engine.RunBatch(dates);
                    break;

                case "replay":
                    if (string.IsNullOrEmpty(date))
                    {
                        System.Console.WriteLine("[ERROR] --date is required for replay mode.");
                        PrintUsage();
                        return 1;
                    }

                    string resolvedScreeningCsv = screeningCsvPath
                        ?? @"C:\Users\User\Documents\_02_bt\Backtest_tick_module\screening_results.csv";
                    string resolvedCloseParquet = closeParquetPath
                        ?? @"D:\03_預估量相關資量\CSharp\close.parquet";
                    string resolvedTickBase = tickDataPath
                        ?? @"D:\feature_data\feature";
                    string resolvedOutputDir = outputPath
                        ?? Path.Combine(config.GetString("output_path", Core.Constants.DefaultOutputDir), "replay");

                    System.Console.WriteLine($"[REPLAY] Screening CSV: {resolvedScreeningCsv}");
                    System.Console.WriteLine($"[REPLAY] Close parquet: {resolvedCloseParquet}");
                    System.Console.WriteLine($"[REPLAY] Tick base: {resolvedTickBase}");
                    System.Console.WriteLine($"[REPLAY] Output: {resolvedOutputDir}");

                    var replayEngine = new ReplayEngine();
                    var replayResult = replayEngine.Run(date, resolvedScreeningCsv, resolvedCloseParquet, resolvedTickBase);

                    if (replayResult.Snapshots.Count > 0)
                    {
                        Directory.CreateDirectory(resolvedOutputDir);
                        string jsonPath = Path.Combine(resolvedOutputDir, $"replay_data.json");
                        ReplayExporter.ExportJson(replayResult, jsonPath);

                        // Copy replay.html to output dir if it exists
                        string htmlSource = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Web", "replay.html");
                        if (!File.Exists(htmlSource))
                            htmlSource = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Web", "replay.html");
                        if (!File.Exists(htmlSource))
                            htmlSource = Path.GetFullPath(Path.Combine("Web", "replay.html"));

                        if (File.Exists(htmlSource))
                        {
                            string htmlDest = Path.Combine(resolvedOutputDir, "replay.html");
                            File.Copy(htmlSource, htmlDest, overwrite: true);
                            System.Console.WriteLine($"[REPLAY] HTML viewer copied to {htmlDest}");
                        }
                        else
                        {
                            System.Console.WriteLine($"[REPLAY] HTML viewer not found at expected locations.");
                            System.Console.WriteLine($"[REPLAY] Copy Web/replay.html manually to {resolvedOutputDir}");
                        }

                        System.Console.WriteLine($"\n[REPLAY] Open replay.html in a browser to view results.");
                        System.Console.WriteLine($"[REPLAY] Output: {resolvedOutputDir}");
                    }
                    break;

                case "chart":
                    if (string.IsNullOrEmpty(stockId) || string.IsNullOrEmpty(date))
                    {
                        System.Console.WriteLine("[ERROR] --stock_id and --date are required for chart mode.");
                        PrintUsage();
                        return 1;
                    }
                    return RunChart(stockId, date, tickDataPath, outputPath);

                default:
                    System.Console.WriteLine($"[ERROR] Unknown mode: {mode}");
                    PrintUsage();
                    return 1;
            }

            System.Console.WriteLine("\n=== Done ===");
            return 0;
        }

        static int RunChart(string stockId, string date, string? tickDataPath, string? outputPath)
        {
            string tickBase = tickDataPath ?? @"D:\feature_data\feature";
            string tickPath = Path.Combine(tickBase, date, $"{stockId}.parquet");

            if (!File.Exists(tickPath))
            {
                System.Console.WriteLine($"[ERROR] Tick data not found: {tickPath}");
                return 1;
            }

            string outDir = outputPath ?? @"D:\03_預估量相關資量";
            Directory.CreateDirectory(outDir);
            string htmlPath = Path.Combine(outDir, $"{stockId}_trade_{date}.html");
            string pngPath = Path.Combine(outDir, $"{stockId}_trade_{date}.png");

            // Hardcoded trade info for 2408 on 2026-01-30
            var trade = new TradeChartGenerator.TradeInfo
            {
                StockId = stockId,
                Date = date,
                EntryTime = DateTime.Parse($"{date} 09:07:11.666931"),
                EntryPrice = 314.50,
                EntryVwap = 309.723768,
                EntryDayHigh = 314.50,
                TotalShares = 31796.0,
                PositionCash = 10_000_000.0,
                GroupName = "記憶體",
                GroupRank = 1,
                MemberRank = 1,
                ExitTime = DateTime.Parse($"{date} 09:51:07.254196"),
                ExitPrice = 310.50,
                ExitReason = "stopLoss",
                PnlAmount = -22260.50,
                PnlPercent = -0.2226,
                StopLossPrice = 309.723768 * 0.995, // VWAP × 0.995
                TpOrders = new List<(string Label, double Price, DateTime? FillTime)>
                {
                    ("TP1", 317.50, (DateTime?)null),
                    ("TP2", 320.00, (DateTime?)null),
                    ("TP3", 314.50 * 1.03, (DateTime?)null) // DayHigh × 1.03
                }
            };

            // Find TP fill times by scanning tick data timestamps
            // TP1@317.50 and TP2@320.00 were filled (ProfitTaken=True)
            // We'll let the chart generator handle the visual — mark fill times as approximate

            System.Console.WriteLine($"[CHART] Generating chart for {stockId} on {date}...");
            TradeChartGenerator.Generate(tickPath, trade, htmlPath, pngPath);
            return 0;
        }

        static void PrintUsage()
        {
            System.Console.WriteLine(@"
Usage:
  dotnet run -- --mode single --date 2024-01-15 [options]
  dotnet run -- --mode batch --dates 2024-01-15 2024-01-16 [options]
  dotnet run -- --mode replay --date 2026-03-06 [options]
  dotnet run -- --mode chart --stock_id 2408 --date 2026-01-30 [options]

Options:
  --mode <single|batch|replay|chart>  Execution mode
  --date <YYYY-MM-DD>           Backtest/replay date
  --dates <d1 d2 ...>           Multiple dates (batch mode)
  --stock_id <id>               Stock ID (chart mode)
  --config <path>               YAML config file path
  --output_path <path>          Override output directory
  --group_csv <path>            Override group.csv path
  --tick_data <path>            Override tick data base path
  --static_data <path>          Override static data parquet path
  --entry_start_time <HH:MM:SS> Override entry start time
  --bypass_group                Bypass group screening (run all given stocks)
  --stock_list <id1 id2 ...>    Direct stock list (use with --bypass_group)
  --screening_csv <path>        Screening results CSV (replay mode)
  --close_parquet <path>        Close prices parquet (replay mode)
  --help                        Show this help
");
        }
    }
}
