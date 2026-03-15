using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BacktestModule.Core;

namespace BacktestModule.ConsoleApp
{
    /// <summary>
    /// BO Reentry Strategy Backtest Program - Modular Version
    /// Main entry point rewriting bo_reentry.py.
    ///
    /// Features:
    /// 1. Price change limit 8.5%
    /// 2. Momentum exhaustion exit mechanism
    /// 3. Re-entry logic: price creates new high + 3-second outside volume comparison
    /// 4. Detailed entry signal logging with each condition's check result
    ///
    /// Usage:
    ///   Single stock mode:
    ///     BacktestModule --mode single --stock_id 2330 --date 2024-01-15
    ///
    ///   Batch mode with stock list:
    ///     BacktestModule --mode batch --date 2024-01-15 --stock_list 2330 2317 2454
    ///
    ///   Batch mode with screening results:
    ///     BacktestModule --mode batch --date 2024-01-15 --use_screening
    ///
    ///   Parameter overrides:
    ///     BacktestModule --mode single --stock_id 2330 --date 2024-01-15 --entry_start_time 09:10:00
    ///     BacktestModule --mode single --stock_id 2330 --date 2024-01-15 --liquidity_multiplier 0.005
    ///
    ///   Dynamic liquidity threshold:
    ///     BacktestModule --mode batch --date 2024-01-15 --stock_list 2330 2317 --use_dynamic_liquidity
    ///     BacktestModule --mode batch --date 2024-01-15 --use_screening --use_dynamic_liquidity --liquidity_cap 30000000
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Loads screening results from CSV file.
        /// Reads the screening results CSV and filters by date.
        /// Returns a list of stock IDs.
        /// </summary>
        private static List<string> LoadScreeningResults(string date, string screeningFile = null)
        {
            try
            {
                string screeningPath = !string.IsNullOrEmpty(screeningFile) ? screeningFile : Constants.ScreeningResultsPath;

                if (!File.Exists(screeningPath))
                {
                    System.Console.WriteLine($"[ERROR] Screening results file not found: {screeningPath}");
                    return new List<string>();
                }

                var lines = File.ReadAllLines(screeningPath);
                if (lines.Length == 0)
                {
                    System.Console.WriteLine("[ERROR] Screening results file is empty.");
                    return new List<string>();
                }

                // Parse CSV header (strip BOM if present)
                string headerLine = lines[0].TrimStart('\uFEFF');
                string[] headers = headerLine.Split(',').Select(h => h.Trim().ToLowerInvariant()).ToArray();
                int dateColIdx = Array.IndexOf(headers, "date");
                if (dateColIdx < 0) dateColIdx = Array.FindIndex(headers, h => h == "日期");
                int stockIdColIdx = Array.IndexOf(headers, "stock_id");
                int codeColIdx = Array.IndexOf(headers, "code");
                if (codeColIdx < 0) codeColIdx = Array.FindIndex(headers, h => h == "代碼");

                // Determine stock column
                int stockCol = stockIdColIdx >= 0 ? stockIdColIdx : codeColIdx;
                if (stockCol < 0)
                {
                    System.Console.WriteLine("[WARNING] Cannot find stock_id or code column in screening results.");
                    return new List<string>();
                }

                var stockList = new List<string>();
                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;

                    string[] cols = lines[i].Split(',');

                    // Filter by date if date column exists
                    if (dateColIdx >= 0 && dateColIdx < cols.Length)
                    {
                        string rowDate = cols[dateColIdx].Trim();
                        if (rowDate != date) continue;
                    }

                    if (stockCol < cols.Length)
                    {
                        string stockId = cols[stockCol].Trim();
                        if (!string.IsNullOrEmpty(stockId))
                            stockList.Add(stockId);
                    }
                }

                System.Console.WriteLine($"[INFO] Loaded {stockList.Count} stocks from screening results.");
                return stockList;
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[ERROR] Failed to load screening results: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// Parses command-line arguments.
        /// Implements the same argument structure as the Python argparse version.
        /// </summary>
        private static Dictionary<string, string> ParseArguments(string[] args)
        {
            var parsed = new Dictionary<string, string>
            {
                ["mode"] = "single",
                ["config"] = Constants.DefaultConfigPath
            };

            var stockListItems = new List<string>();
            bool collectingStockList = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                // If we were collecting stock list items, check if this is a new flag
                if (collectingStockList)
                {
                    if (arg.StartsWith("--"))
                    {
                        collectingStockList = false;
                        // Fall through to process as flag
                    }
                    else
                    {
                        stockListItems.Add(arg);
                        continue;
                    }
                }

                switch (arg)
                {
                    case "--mode":
                        if (i + 1 < args.Length) parsed["mode"] = args[++i];
                        break;
                    case "--stock_id":
                        if (i + 1 < args.Length) parsed["stock_id"] = args[++i];
                        break;
                    case "--date":
                        if (i + 1 < args.Length) parsed["date"] = args[++i];
                        break;
                    case "--stock_list":
                        collectingStockList = true;
                        break;
                    case "--use_screening":
                        parsed["use_screening"] = "true";
                        break;
                    case "--no_csv":
                        parsed["no_csv"] = "true";
                        break;
                    case "--no_chart":
                        parsed["no_chart"] = "true";
                        break;
                    case "--config":
                        if (i + 1 < args.Length) parsed["config"] = args[++i];
                        break;
                    case "--entry_start_time":
                        if (i + 1 < args.Length) parsed["entry_start_time"] = args[++i];
                        break;
                    case "--liquidity_multiplier":
                        if (i + 1 < args.Length) parsed["liquidity_multiplier"] = args[++i];
                        break;
                    case "--use_dynamic_liquidity":
                        parsed["use_dynamic_liquidity"] = "true";
                        break;
                    case "--liquidity_cap":
                        if (i + 1 < args.Length) parsed["liquidity_cap"] = args[++i];
                        break;
                    case "--output_path":
                        if (i + 1 < args.Length) parsed["output_path"] = args[++i];
                        break;
                    case "--screening_file":
                        if (i + 1 < args.Length) parsed["screening_file"] = args[++i];
                        break;
                    case "--no_ask_wall":
                        parsed["no_ask_wall"] = "true";
                        break;
                    case "--stop_loss_ticks_large":
                        if (i + 1 < args.Length) parsed["stop_loss_ticks_large"] = args[++i];
                        break;
                    case "--ma5_bias5":
                        parsed["ma5_bias5"] = "true";
                        break;
                    case "--ma5_bias7":
                        parsed["ma5_bias7"] = "true";
                        break;
                    case "--ma5_bias3":
                        parsed["ma5_bias3"] = "true";
                        break;
                    case "--low_ratio":
                        parsed["low_ratio"] = "true";
                        break;
                    case "--help":
                    case "-h":
                        PrintUsage();
                        parsed["_exit"] = "true";
                        return parsed;
                    default:
                        System.Console.WriteLine($"[WARNING] Unknown argument: {arg}");
                        break;
                }
            }

            if (stockListItems.Count > 0)
            {
                parsed["stock_list"] = string.Join(",", stockListItems);
            }

            return parsed;
        }

        /// <summary>
        /// Prints usage information.
        /// </summary>
        private static void PrintUsage()
        {
            System.Console.WriteLine(@"
BO Reentry Strategy Backtest Program

Usage:
  BacktestModule [options]

Options:
  --mode <single|batch>       Execution mode: single (one stock) or batch (multiple stocks)
                              Default: single
  --stock_id <id>             Stock ID (required for single mode)
  --date <YYYY-MM-DD>         Backtest date (required)
  --stock_list <id1 id2 ...>  Stock ID list (for batch mode)
  --use_screening             Use screening results file (for batch mode)
  --no_csv                    Do not output CSV files
  --no_chart                  Do not generate charts
  --config <path>             Strategy config file path
                              Default: Bo_v2.yaml
  --entry_start_time <HH:MM:SS>  Override entry start time
  --liquidity_multiplier <value> Override liquidity threshold multiplier
  --use_dynamic_liquidity     Enable dynamic massive matching threshold
                              (reads from daily_liquidity_threshold.parquet)
  --liquidity_cap <value>     Dynamic liquidity threshold cap (default: from config)
  --no_ask_wall               Disable ask wall exit (override config)
  --stop_loss_ticks_large <N> Override strategy_b_stop_loss_ticks_large
  --ma5_bias5                 Enable MA5 bias threshold filter (price >= MA5*1.05)
  --ma5_bias7                 Enable MA5 bias threshold filter (price >= MA5*1.07)
  --ma5_bias3                 Enable MA5 bias threshold filter (price >= MA5*1.03)
  --low_ratio                 Enable low ratio filter (low_3m/low_15m > 1.005)
  -h, --help                  Show this help message
");
        }

        /// <summary>
        /// Main entry point.
        /// </summary>
        public static void Main(string[] args)
        {
            // Parse arguments
            var parsed = ParseArguments(args);

            // Exit if help was requested
            if (parsed.ContainsKey("_exit"))
                return;

            // Validate required arguments
            if (!parsed.ContainsKey("date"))
            {
                System.Console.WriteLine("[ERROR] --date is required. Use --help for usage information.");
                return;
            }

            string mode = parsed["mode"];
            string date = parsed["date"];
            string configPath = parsed["config"];
            bool noChart = parsed.ContainsKey("no_chart");
            bool noCsv = parsed.ContainsKey("no_csv");

            // Create backtest engine
            var engine = new BacktestEngine(configPath);

            // Override output path if provided (CLI arg or environment variable)
            if (parsed.TryGetValue("output_path", out string outputPath))
            {
                engine.Config.RawConfig["output_path"] = outputPath;
                System.Console.WriteLine($"[INFO] Override output path: {outputPath}");
            }
            else
            {
                string envOutputPath = Environment.GetEnvironmentVariable("BACKTEST_OUTPUT_PATH");
                if (!string.IsNullOrEmpty(envOutputPath))
                {
                    engine.Config.RawConfig["output_path"] = envOutputPath;
                    System.Console.WriteLine($"[INFO] Override output path (env): {envOutputPath}");
                }
            }

            // Override config parameters if provided
            if (parsed.TryGetValue("entry_start_time", out string entryStartTimeStr))
            {
                if (TimeSpan.TryParseExact(entryStartTimeStr, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var entryTime)
                    || TimeSpan.TryParse(entryStartTimeStr, out entryTime))
                {
                    // Override in the raw config (affects future GetEntryConfig calls)
                    engine.Config.RawConfig["entry_start_time"] = entryTime;
                    // Also override in the already-extracted entry config
                    engine.EntryConfig["entry_start_time"] = entryTime;
                    System.Console.WriteLine($"[INFO] Override entry start time: {entryStartTimeStr}");
                }
                else
                {
                    System.Console.WriteLine($"[ERROR] Invalid time format: {entryStartTimeStr}, please use HH:MM:SS format.");
                    return;
                }
            }

            if (parsed.TryGetValue("liquidity_multiplier", out string liquidityStr))
            {
                if (double.TryParse(liquidityStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double multiplier))
                {
                    engine.Config.RawConfig["dynamic_liquidity_multiplier"] = multiplier;
                    engine.EntryConfig["dynamic_liquidity_multiplier"] = multiplier;
                    System.Console.WriteLine($"[INFO] Override liquidity threshold multiplier: {multiplier}");
                }
                else
                {
                    System.Console.WriteLine($"[ERROR] Invalid liquidity multiplier value: {liquidityStr}");
                    return;
                }
            }

            // Enable dynamic liquidity threshold (mirrors Python --use_dynamic_liquidity)
            if (parsed.ContainsKey("use_dynamic_liquidity"))
            {
                engine.Config.RawConfig["use_dynamic_liquidity_threshold"] = true;
                engine.EntryConfig["use_dynamic_liquidity_threshold"] = true;

                // Apply liquidity cap override if provided
                if (parsed.TryGetValue("liquidity_cap", out string capStr))
                {
                    if (double.TryParse(capStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double cap))
                    {
                        engine.Config.RawConfig["dynamic_liquidity_threshold_cap"] = cap;
                        engine.EntryConfig["dynamic_liquidity_threshold_cap"] = cap;
                        System.Console.WriteLine($"[INFO] Override liquidity threshold cap: {cap:N0} ({cap / 1_000_000:F0}M)");
                    }
                    else
                    {
                        System.Console.WriteLine($"[ERROR] Invalid liquidity cap value: {capStr}");
                        return;
                    }
                }

                double effectiveMultiplier = engine.EntryConfig.TryGetValue("dynamic_liquidity_multiplier", out var dm)
                    ? Convert.ToDouble(dm) : 0.004;
                double effectiveCap = engine.EntryConfig.TryGetValue("dynamic_liquidity_threshold_cap", out var dc)
                    ? Convert.ToDouble(dc) : 50_000_000.0;

                System.Console.WriteLine($"[INFO] Enabled dynamic liquidity threshold");
                System.Console.WriteLine($"[INFO]   Multiplier: {effectiveMultiplier}, Cap: {effectiveCap:N0} ({effectiveCap / 1_000_000:F0}M)");
            }

            // Override ask_wall_exit_enabled if --no_ask_wall
            if (parsed.ContainsKey("no_ask_wall"))
            {
                engine.ExitConfig["ask_wall_exit_enabled"] = false;
                System.Console.WriteLine("[INFO] Override: ask_wall_exit_enabled = false");
            }

            // Override strategy_b_stop_loss_ticks_large if --stop_loss_ticks_large
            if (parsed.TryGetValue("stop_loss_ticks_large", out string ticksLargeStr))
            {
                if (int.TryParse(ticksLargeStr, out int ticksLarge))
                {
                    engine.ExitConfig["strategy_b_stop_loss_ticks_large"] = ticksLarge;
                    System.Console.WriteLine($"[INFO] Override: strategy_b_stop_loss_ticks_large = {ticksLarge}");
                }
                else
                {
                    System.Console.WriteLine($"[ERROR] Invalid stop_loss_ticks_large value: {ticksLargeStr}");
                    return;
                }
            }

            // Enable MA5 bias threshold (--ma5_bias5 or --ma5_bias7)
            if (parsed.ContainsKey("ma5_bias5"))
            {
                engine.Config.RawConfig["ma5_bias5_enabled"] = true;
                engine.EntryConfig["ma5_bias5_enabled"] = true;
                System.Console.WriteLine("[INFO] Enabled MA5 bias threshold filter (bias5)");
            }
            if (parsed.ContainsKey("ma5_bias7"))
            {
                engine.Config.RawConfig["ma5_bias5_enabled"] = true;
                engine.EntryConfig["ma5_bias5_enabled"] = true;
                engine.Config.RawConfig["ma5_bias5_file"] = "ma5_bias7_threshold.parquet";
                engine.EntryConfig["ma5_bias5_file"] = "ma5_bias7_threshold.parquet";
                System.Console.WriteLine("[INFO] Enabled MA5 bias threshold filter (bias7)");
            }
            if (parsed.ContainsKey("ma5_bias3"))
            {
                engine.Config.RawConfig["ma5_bias5_enabled"] = true;
                engine.EntryConfig["ma5_bias5_enabled"] = true;
                engine.Config.RawConfig["ma5_bias5_file"] = "ma5_bias3_threshold.parquet";
                engine.EntryConfig["ma5_bias5_file"] = "ma5_bias3_threshold.parquet";
                System.Console.WriteLine("[INFO] Enabled MA5 bias threshold filter (bias3)");
            }
            if (parsed.ContainsKey("low_ratio"))
            {
                engine.Config.RawConfig["low_ratio_filter_enabled"] = true;
                engine.EntryConfig["low_ratio_filter_enabled"] = true;
                System.Console.WriteLine("[INFO] Enabled low ratio filter (low_3m/low_15m > threshold)");
            }

            if (mode == "single")
            {
                // Single stock backtest mode
                if (!parsed.ContainsKey("stock_id"))
                {
                    System.Console.WriteLine("[ERROR] Single stock mode requires --stock_id.");
                    return;
                }

                string stockId = parsed["stock_id"];

                System.Console.WriteLine($"\n{"",60}");
                System.Console.WriteLine("Running single stock backtest");
                System.Console.WriteLine($"Stock: {stockId}, Date: {date}");
                System.Console.WriteLine($"{"",60}\n");

                var trades = engine.RunSingleBacktest(
                    stockId: stockId,
                    date: date,
                    silent: noChart);

                if (trades != null && trades.Count > 0)
                {
                    System.Console.WriteLine($"\nBacktest complete. {trades.Count} trade(s) recorded.");
                }
                else
                {
                    System.Console.WriteLine("\nBacktest complete. No trades recorded.");
                }
            }
            else if (mode == "batch")
            {
                // Batch backtest mode
                var stockList = new List<string>();

                if (parsed.ContainsKey("use_screening"))
                {
                    // Use screening results
                    string screeningFile = parsed.TryGetValue("screening_file", out string sf) ? sf : null;
                    stockList = LoadScreeningResults(date, screeningFile);
                }
                else if (parsed.TryGetValue("stock_list", out string stockListStr))
                {
                    // Use specified stock list
                    stockList = stockListStr.Split(',')
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();
                }
                else
                {
                    System.Console.WriteLine("[ERROR] Batch mode requires --stock_list or --use_screening.");
                    return;
                }

                if (stockList.Count == 0)
                {
                    System.Console.WriteLine("[ERROR] No stocks available for backtesting.");
                    return;
                }

                System.Console.WriteLine($"\n{"",60}");
                System.Console.WriteLine("Running batch backtest");
                System.Console.WriteLine($"Stock count: {stockList.Count}, Date: {date}");
                System.Console.WriteLine($"{"",60}\n");

                var results = engine.RunBatchBacktest(
                    stockList: stockList,
                    date: date,
                    outputCsv: !noCsv,
                    createCharts: !noChart);

                if (results != null && results.Count > 0)
                {
                    System.Console.WriteLine($"\nBatch backtest complete. {results.Count} stock(s) had trades.");
                }
                else
                {
                    System.Console.WriteLine("\nBatch backtest complete. No trades recorded.");
                }
            }
            else
            {
                System.Console.WriteLine($"[ERROR] Unknown mode: {mode}. Use 'single' or 'batch'.");
                return;
            }

            System.Console.WriteLine("\nProgram execution complete.");
        }
    }
}
