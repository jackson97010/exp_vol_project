using System;
using System.Collections.Generic;
using System.IO;
using StrongestVwap.Core.Models;
using StrongestVwap.Strategy;
using StrongestVwap.Analytics;
using StrongestVwap.Exporters;

namespace StrongestVwap.Core
{
    /// <summary>
    /// Orchestrator: loads data, runs backtest loop, outputs results.
    /// </summary>
    public class BacktestEngine
    {
        private readonly StrategyConfig _config;
        private readonly string _outputPath;
        private readonly string _groupCsvPath;
        private readonly string _tickDataBasePath;
        private readonly string? _screeningCsvPath;

        // Dynamic liquidity threshold cache (wide-format: date -> stockId -> threshold)
        private Dictionary<string, Dictionary<string, double>>? _liquidityCache;

        // Direct stock list (bypass group screening)
        private List<string>? _directStockList;

        public BacktestEngine(StrategyConfig config)
        {
            _config = config;
            _outputPath = config.GetString("output_path", Constants.DefaultOutputDir);
            _groupCsvPath = config.GetString("group_csv_path", Constants.DefaultGroupCsvPath);
            _tickDataBasePath = config.GetString("tick_data_base_path", Constants.TickDataBasePath);
            _screeningCsvPath = config.GetString("screening_csv_path", "");
            if (string.IsNullOrEmpty(_screeningCsvPath))
                _screeningCsvPath = null;

            // Load dynamic liquidity threshold if needed
            if (config.GetBool("massive_matching_enabled", false) &&
                config.GetBool("use_dynamic_liquidity_threshold", false))
            {
                LoadLiquidityThreshold();
            }
        }

        /// <summary>
        /// Set direct stock list for bypass_group_screening mode.
        /// All stocks will be placed in a single "ALL" group.
        /// </summary>
        public void SetDirectStockList(List<string> stockIds)
        {
            _directStockList = stockIds;
        }

        private void LoadLiquidityThreshold()
        {
            string basePath = "D:\\03_預估量相關資量\\CSharp";
            string compatPath = Path.Combine(basePath, "daily_liquidity_threshold_compat.parquet");
            string normalPath = Path.Combine(basePath, "daily_liquidity_threshold.parquet");
            string path = File.Exists(compatPath) ? compatPath : normalPath;

            if (File.Exists(path))
            {
                _liquidityCache = DataLoader.LoadLiquidityThreshold(path);
                Console.WriteLine($"[ENGINE] Loaded dynamic liquidity thresholds: {_liquidityCache.Count} dates");
            }
            else
            {
                Console.WriteLine($"[WARNING] Liquidity threshold file not found at {path}");
            }
        }

        /// <summary>
        /// Get the dynamic liquidity threshold for a specific stock on a specific date.
        /// Returns 0 if not found.
        /// </summary>
        public double GetDynamicThreshold(string date, string stockId)
        {
            if (_liquidityCache == null) return 0;
            if (_liquidityCache.TryGetValue(date, out var stocks) &&
                stocks.TryGetValue(stockId, out double threshold))
                return threshold;
            return 0;
        }

        /// <summary>
        /// Run backtest for a single date.
        /// </summary>
        public List<TradeRecord> RunSingleDate(string date, string? tickDataPath = null, string? staticDataPath = null)
        {
            Console.WriteLine($"\n{'=',-60}");
            Console.WriteLine($"[ENGINE] Running backtest for date: {date}");
            Console.WriteLine($"{'=',-60}");

            // Load group definitions
            Dictionary<string, GroupDefinition> groups;
            bool bypassGroup = _config.GetBool("bypass_group_screening", false);

            if (bypassGroup)
            {
                // Bypass mode: collect stock IDs from CLI --stock_list or screening CSV
                List<string> stockIds;
                if (_directStockList != null && _directStockList.Count > 0)
                {
                    stockIds = _directStockList;
                }
                else if (_screeningCsvPath != null)
                {
                    stockIds = DataLoader.LoadStockIdsFromScreeningCsv(_screeningCsvPath, date);
                }
                else
                {
                    Console.WriteLine("[ENGINE] Bypass group mode requires --stock_list or --screening_csv.");
                    return new List<TradeRecord>();
                }

                if (stockIds.Count == 0)
                {
                    Console.WriteLine($"[ENGINE] No stocks found for {date} in bypass mode. Skipping.");
                    return new List<TradeRecord>();
                }

                groups = new Dictionary<string, GroupDefinition>
                {
                    ["ALL"] = new GroupDefinition
                    {
                        GroupName = "ALL",
                        MemberStockIds = new List<string>(stockIds)
                    }
                };
                Console.WriteLine($"[ENGINE] Bypass group mode: {stockIds.Count} stocks in ALL group");
            }
            else if (_screeningCsvPath != null)
                groups = DataLoader.LoadGroupsFromScreeningCsv(_screeningCsvPath, date);
            else
                groups = DataLoader.LoadGroupCsv(_groupCsvPath);

            if (groups.Count == 0)
            {
                Console.WriteLine("[ENGINE] No groups loaded. Aborting.");
                return new List<TradeRecord>();
            }

            // Load static data (try parquet first, fallback to csv)
            string resolvedStaticPath;
            if (staticDataPath != null)
                resolvedStaticPath = staticDataPath;
            else
            {
                string basePath = tickDataPath ?? _tickDataBasePath;
                string parquetPath = Path.Combine(basePath, date, "static_data.parquet");
                string csvPath = Path.Combine(basePath, date, "static_data.csv");
                resolvedStaticPath = File.Exists(parquetPath) ? parquetPath : csvPath;
            }
            var staticData = DataLoader.LoadStaticData(resolvedStaticPath);
            Console.WriteLine($"[ENGINE] Loaded static data for {staticData.Count} stocks");

            // Load tick data
            List<RawTick> ticks;
            string resolvedTickBase = tickDataPath ?? _tickDataBasePath;

            // If tick path is a directory (per-stock files) or points to a .parquet file
            string perStockDir = Path.Combine(resolvedTickBase, date);
            if (Directory.Exists(perStockDir) && !File.Exists(Path.Combine(perStockDir, "all_ticks.parquet")))
            {
                // Per-stock mode: load from individual {stockId}.parquet files
                var allStockIds = new HashSet<string>();
                foreach (var gd in groups.Values)
                    foreach (var sid in gd.MemberStockIds)
                        allStockIds.Add(sid);

                bool loadDepth = _config.GetBool("require_ask_gt_bid", false);
                ticks = DataLoader.LoadPerStockTickData(resolvedTickBase, date, allStockIds, staticData, loadDepth);
            }
            else
            {
                // Single merged file mode
                string resolvedTickPath = File.Exists(resolvedTickBase)
                    ? resolvedTickBase
                    : Path.Combine(resolvedTickBase, date, "all_ticks.parquet");
                ticks = DataLoader.LoadTickDataParquet(resolvedTickPath);
            }
            Console.WriteLine($"[ENGINE] Loaded {ticks.Count} ticks");

            if (ticks.Count == 0)
            {
                Console.WriteLine("[ENGINE] No ticks loaded. Aborting.");
                return new List<TradeRecord>();
            }

            // Run backtest loop
            var loop = new BacktestLoop(_config, groups, staticData, date, this);
            loop.Run(ticks);

            // Get results
            var trades = new List<TradeRecord>(loop.PositionManager.CompletedTrades);

            // Output results
            GenerateOutputs(date, trades);

            return trades;
        }

        /// <summary>
        /// Run backtest for multiple dates.
        /// </summary>
        public List<TradeRecord> RunBatch(List<string> dates)
        {
            var allTrades = new List<TradeRecord>();

            foreach (var date in dates)
            {
                try
                {
                    var trades = RunSingleDate(date);
                    allTrades.AddRange(trades);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ENGINE] Error on date {date}: {ex.Message}");
                }
            }

            // Generate aggregate report
            if (allTrades.Count > 0)
            {
                Console.WriteLine($"\n{'=',-60}");
                Console.WriteLine($"[ENGINE] Batch complete: {allTrades.Count} trades across {dates.Count} dates");
                TradeStatistics.PrintSummary(allTrades);

                string batchParquetPath = Path.Combine(_outputPath, "batch_trades.parquet");
                ParquetExporter.ExportTrades(allTrades, batchParquetPath);
            }

            return allTrades;
        }

        private void GenerateOutputs(string date, List<TradeRecord> trades)
        {
            if (trades.Count == 0)
            {
                Console.WriteLine("[ENGINE] No trades to output.");
                return;
            }

            // Print summary
            TradeStatistics.PrintSummary(trades);

            // Export per-date parquet file (all trades for this date in one file)
            string dateOutputDir = Path.Combine(_outputPath, date);
            string dateParquetPath = Path.Combine(dateOutputDir, $"trades_{date}.parquet");
            ParquetExporter.ExportTrades(trades, dateParquetPath);
        }
    }
}
