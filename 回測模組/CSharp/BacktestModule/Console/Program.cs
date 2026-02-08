using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BacktestModule.Core;

namespace BacktestModule.Console
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
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Loads screening results from CSV file.
        /// Reads the screening results CSV and filters by date.
        /// Returns a list of stock IDs.
        /// </summary>
        private static List<string> LoadScreeningResults(string date)
        {
            try
            {
                string screeningPath = Constants.ScreeningResultsPath;

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

                // Parse CSV header
                string[] headers = lines[0].Split(',').Select(h => h.Trim().ToLowerInvariant()).ToArray();
                int dateColIdx = Array.IndexOf(headers, "date");
                int stockIdColIdx = Array.IndexOf(headers, "stock_id");
                int codeColIdx = Array.IndexOf(headers, "code");

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
                    stockList = LoadScreeningResults(date);
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
