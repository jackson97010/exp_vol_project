using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BacktestModule.Core.Models;
using BacktestModule.Strategy;

namespace BacktestModule.Core
{
    /// <summary>
    /// Core backtest engine: orchestrates all modules.
    /// Creates and initializes all sub-modules, then delegates to BacktestLoop.
    /// </summary>
    public class BacktestEngine
    {
        public StrategyConfig Config { get; }
        public Dictionary<string, object> EntryConfig { get; }
        public Dictionary<string, object> ExitConfig { get; }
        public Dictionary<string, object> ReentryConfig { get; }

        // Core modules
        public DataProcessor DataProcessor { get; }
        public EntryChecker EntryChecker { get; }
        public ExitManager ExitManager { get; }
        public PositionManager PositionManager { get; }
        public ReentryManager ReentryManager { get; }

        // Indicators
        public DayHighMomentumTracker MomentumTracker { get; }
        public OrderBookBalanceMonitor OrderbookMonitor { get; }
        public OutsideVolumeTracker OutsideVolumeTracker { get; }
        public OutsideVolumeTracker OutsideVolumeTracker5s { get; }
        public MassiveMatchingTracker MassiveMatchingTracker { get; }
        public InsideOutsideRatioTracker InsideOutsideRatioTracker { get; }
        public InsideOutsideRatioTracker LargeOrderIoRatioTracker { get; }

        // Entry signal log
        public List<Dictionary<string, object>> EntrySignalLog { get; } = new();

        // Last tick data (for chart generation)
        public List<TickData> LastTickData { get; private set; }

        // Dynamic liquidity threshold cache (wide-format: date -> stockId -> threshold)
        private Dictionary<DateTime, Dictionary<string, double>> _liquidityThresholdCache;
        // O(1) lookup: date-only -> actual cache key (avoids linear scan)
        private Dictionary<DateTime, DateTime> _dateOnlyLookup;

        // MA5 bias threshold cache (wide-format: date -> stockId -> threshold price)
        private Dictionary<DateTime, Dictionary<string, double>> _ma5BiasCache;
        private Dictionary<DateTime, DateTime> _ma5BiasDateLookup;

        public BacktestEngine(string configPath = "configs/Bo_v2.yaml")
        {
            // Load configuration
            Config = new StrategyConfig(configPath);
            EntryConfig = Config.GetEntryConfig();
            ExitConfig = Config.GetExitConfig();
            ReentryConfig = Config.GetReentryConfig();

            // Initialize core modules
            DataProcessor = new DataProcessor(Config.GetAllConfig());
            EntryChecker = new EntryChecker(EntryConfig);
            ExitManager = new ExitManager(ExitConfig);
            PositionManager = new PositionManager();
            ReentryManager = new ReentryManager(ReentryConfig);

            // Initialize indicators
            MomentumTracker = new DayHighMomentumTracker(windowSeconds: Constants.DayHighMomentumWindow);
            OrderbookMonitor = new OrderBookBalanceMonitor(
                thinThreshold: Constants.OrderBookThinThreshold,
                normalThreshold: Constants.OrderBookNormalThreshold);
            OutsideVolumeTracker = new OutsideVolumeTracker(windowSeconds: Constants.OutsideVolumeWindow);
            OutsideVolumeTracker5s = new OutsideVolumeTracker(windowSeconds: Constants.OutsideVolumeWindow5s);
            int massiveMatchingWindow = EntryConfig.TryGetValue("massive_matching_window", out var mmw)
                ? Convert.ToInt32(mmw) : Constants.MassiveMatchingWindow;
            MassiveMatchingTracker = new MassiveMatchingTracker(windowSeconds: massiveMatchingWindow);
            InsideOutsideRatioTracker = new InsideOutsideRatioTracker(windowSeconds: Constants.IoRatioWindow);
            LargeOrderIoRatioTracker = new InsideOutsideRatioTracker(
                windowSeconds: Constants.IoRatioWindow,
                minVolumeThreshold: Constants.LargeOrderThreshold);
        }

        /// <summary>
        /// Creates a per-stock engine clone that shares read-only caches.
        /// Each clone has its own mutable state (PositionManager, trackers, etc.).
        /// </summary>
        private BacktestEngine(BacktestEngine parent)
        {
            Config = parent.Config;
            // Clone entry config (mutable: dynamic_liquidity_resolved_threshold is written per-stock)
            EntryConfig = new Dictionary<string, object>(parent.EntryConfig);
            ExitConfig = parent.ExitConfig;  // Read-only after init
            ReentryConfig = parent.ReentryConfig;  // Read-only after init

            // Fresh mutable modules per-stock
            DataProcessor = parent.DataProcessor;  // Stateless, safe to share
            EntryChecker = new EntryChecker(EntryConfig);
            ExitManager = new ExitManager(ExitConfig);
            PositionManager = new PositionManager();
            ReentryManager = new ReentryManager(ReentryConfig);

            // Fresh indicators per-stock
            MomentumTracker = new DayHighMomentumTracker(windowSeconds: Constants.DayHighMomentumWindow);
            OrderbookMonitor = new OrderBookBalanceMonitor(
                thinThreshold: Constants.OrderBookThinThreshold,
                normalThreshold: Constants.OrderBookNormalThreshold);
            OutsideVolumeTracker = new OutsideVolumeTracker(windowSeconds: Constants.OutsideVolumeWindow);
            OutsideVolumeTracker5s = new OutsideVolumeTracker(windowSeconds: Constants.OutsideVolumeWindow5s);
            int cloneMmWindow = EntryConfig.TryGetValue("massive_matching_window", out var cmw)
                ? Convert.ToInt32(cmw) : Constants.MassiveMatchingWindow;
            MassiveMatchingTracker = new MassiveMatchingTracker(windowSeconds: cloneMmWindow);
            InsideOutsideRatioTracker = new InsideOutsideRatioTracker(windowSeconds: Constants.IoRatioWindow);
            LargeOrderIoRatioTracker = new InsideOutsideRatioTracker(
                windowSeconds: Constants.IoRatioWindow,
                minVolumeThreshold: Constants.LargeOrderThreshold);

            // Share read-only caches
            _liquidityThresholdCache = parent._liquidityThresholdCache;
            _dateOnlyLookup = parent._dateOnlyLookup;
            _ma5BiasCache = parent._ma5BiasCache;
            _ma5BiasDateLookup = parent._ma5BiasDateLookup;
        }

        /// <summary>
        /// Run backtest for a single stock on a single date.
        /// </summary>
        public List<TradeRecord> RunSingleBacktest(string stockId, string date, bool silent = false)
        {
            // Reset all state for new stock
            PositionManager.Reset();
            MomentumTracker.Reset();
            OutsideVolumeTracker.Reset();
            OutsideVolumeTracker5s.Reset();
            MassiveMatchingTracker.Reset();
            InsideOutsideRatioTracker.Reset();
            LargeOrderIoRatioTracker.Reset();

            System.Console.WriteLine($"\n{"",60}");
            System.Console.WriteLine($"Starting backtest: {stockId} - {date}");
            System.Console.WriteLine($"{"",60}");

            // 1. Load and process data
            var df = LoadAndProcessData(stockId, date);
            if (df == null || df.Count == 0)
                return new List<TradeRecord>();

            // 2. Get price info
            var (refPrice, limitUpPrice, limitDownPrice) = GetPriceInfo(df, stockId, date);

            // 2.5 Resolve dynamic liquidity threshold for this stock/date
            ResolveDynamicLiquidityThreshold(stockId, date);

            // 2.6 Resolve MA5 bias threshold for this stock/date
            ResolveMa5BiasThreshold(stockId, date);

            // 3. Run backtest loop
            var backtestLoop = new BacktestLoop(this);
            var trades = backtestLoop.Run(df, stockId, refPrice, limitUpPrice);

            // 3.5 Split entry post-processing (Lot B)
            var splitProcessor = new SplitEntryProcessor(EntryConfig, ExitManager);
            var lotBTrades = splitProcessor.Process(df, trades, stockId, limitUpPrice);
            if (lotBTrades.Count > 0)
            {
                AssignTradeGroups(trades, lotBTrades);
                trades = trades.Concat(lotBTrades).ToList();
            }
            else
            {
                // Even without Lot B, assign group_id to Lot A trades
                for (int i = 0; i < trades.Count; i++)
                {
                    if (trades[i].GroupId == null)
                        trades[i].GroupId = i + 1;
                }
            }

            // 4. Generate outputs
            // CSV trade details are always exported; charts only when not silent
            GenerateOutputs(df, trades, stockId, date, refPrice, limitUpPrice, skipChart: silent);

            // Save tick data for chart use
            LastTickData = df;

            return trades;
        }

        /// <summary>
        /// Pre-loads the liquidity threshold cache so parallel workers can share it.
        /// Call before RunBatchBacktest to avoid redundant loading.
        /// </summary>
        public void PreloadLiquidityCache()
        {
            if (_liquidityThresholdCache != null) return;

            var allConfig = Config.GetAllConfig();
            bool useDynamic = allConfig.TryGetValue("use_dynamic_liquidity_threshold", out var ud) && ud is bool b && b;
            if (!useDynamic) return;

            // Trigger cache loading by calling ResolveDynamicLiquidityThreshold with a dummy
            ResolveDynamicLiquidityThreshold("__preload__", "2000-01-01");

            // Also preload MA5 bias cache if enabled
            PreloadMa5BiasCache();
        }

        /// <summary>
        /// Pre-loads the MA5 bias threshold cache so parallel workers can share it.
        /// </summary>
        private void PreloadMa5BiasCache()
        {
            if (_ma5BiasCache != null) return;

            var allConfig = Config.GetAllConfig();
            bool enabled = allConfig.TryGetValue("ma5_bias5_enabled", out var e) && e is bool b && b;
            if (!enabled) return;

            ResolveMa5BiasThreshold("__preload__", "2000-01-01");
        }

        /// <summary>
        /// Run batch backtest for multiple stocks.
        /// Uses Parallel.ForEach for concurrent per-stock processing.
        /// </summary>
        public List<Dictionary<string, object>> RunBatchBacktest(
            List<string> stockList, string date,
            bool outputCsv = true, bool createCharts = true)
        {
            // Pre-load shared read-only caches before parallel execution
            PreloadLiquidityCache();
            // Pre-load company name cache (DataProcessor lazy init is not thread-safe)
            DataProcessor.GetCompanyName("__preload__");

            int maxParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
            var results = new ConcurrentBag<Dictionary<string, object>>();
            var lockObj = new object();

            Parallel.ForEach(stockList,
                new ParallelOptions { MaxDegreeOfParallelism = maxParallelism },
                stockId =>
                {
                    // Create per-stock engine clone with fresh mutable state
                    var perStockEngine = new BacktestEngine(this);

                    lock (lockObj)
                    {
                        System.Console.WriteLine($"\nProcessing stock: {stockId}");
                    }

                    var trades = perStockEngine.RunSingleBacktest(stockId, date, silent: !createCharts);

                    if (trades.Count > 0)
                    {
                        var stats = perStockEngine.CalculateStatistics(stockId, trades, date);
                        results.Add(stats);
                    }
                });

            var resultList = results.ToList();

            // Summary
            if (resultList.Count > 0)
            {
                System.Console.WriteLine($"\nBatch backtest complete. {resultList.Count} stocks had trades.");
            }

            return resultList;
        }

        /// <summary>
        /// Resolves the dynamic liquidity threshold for a specific stock/date.
        /// Loads wide-format parquet (dates as rows, stock IDs as columns) if not cached.
        /// Applies multiplier and cap, then stores in EntryConfig as "dynamic_liquidity_resolved_threshold".
        /// Mirrors Python entry_logic.py: threshold_df.loc[date_key, stock_id] * multiplier, capped.
        /// </summary>
        private void ResolveDynamicLiquidityThreshold(string stockId, string date)
        {
            var allConfig = Config.GetAllConfig();
            bool useDynamic = allConfig.TryGetValue("use_dynamic_liquidity_threshold", out var ud) && ud is bool b && b;
            if (!useDynamic) return;

            // Lazy-load the threshold cache
            if (_liquidityThresholdCache == null)
            {
                string thresholdFile = allConfig.TryGetValue("dynamic_liquidity_threshold_file", out var tf)
                    ? tf.ToString() : "daily_liquidity_threshold.parquet";

                // Try multiple paths (including the project directory and exe directory)
                // Prefer _compat version (stripped SizeStatistics for Parquet.Net 4.x compatibility)
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string compatFile = thresholdFile.Replace(".parquet", "_compat.parquet");
                var paths = new[]
                {
                    compatFile,
                    Path.Combine(@"D:\03_預估量相關資量\CSharp", compatFile),
                    Path.Combine(@"C:\Users\User\Documents\_02_bt\Backtest_tick_module", compatFile),
                    thresholdFile,
                    Path.Combine(@"D:\03_預估量相關資量\CSharp", thresholdFile),
                    Path.Combine(@"D:\feature_data", thresholdFile),
                    Path.Combine(@"C:\Users\User\Documents\_02_bt\Backtest_tick_module", thresholdFile),
                    Path.Combine(exeDir, thresholdFile)
                };

                foreach (var path in paths)
                {
                    if (File.Exists(path))
                    {
                        _liquidityThresholdCache = Strategy.ParquetHelper.ReadLiquidityThresholdParquet(path);
                        System.Console.WriteLine($"[INFO] Loaded liquidity thresholds from: {path}");
                        break;
                    }
                }

                _liquidityThresholdCache ??= new Dictionary<DateTime, Dictionary<string, double>>();

                // Build O(1) date-only lookup
                _dateOnlyLookup = new Dictionary<DateTime, DateTime>(_liquidityThresholdCache.Count);
                foreach (var cachedDate in _liquidityThresholdCache.Keys)
                {
                    _dateOnlyLookup[cachedDate.Date] = cachedDate;
                }
            }

            // Look up threshold for this stock/date
            // Parse the date string to DateTime, then find matching date in cache
            double resolvedThreshold = 0;
            bool found = false;

            if (DateTime.TryParse(date, out DateTime targetDate))
            {
                // O(1) date lookup using pre-built index
                if (_dateOnlyLookup != null &&
                    _dateOnlyLookup.TryGetValue(targetDate.Date, out DateTime matchedDate) &&
                    _liquidityThresholdCache.TryGetValue(matchedDate, out var stockThresholds) &&
                    stockThresholds.TryGetValue(stockId, out double rawThreshold))
                {
                    double multiplier = allConfig.TryGetValue("dynamic_liquidity_multiplier", out var dm)
                        ? Convert.ToDouble(dm) : 0.004;
                    double cap = allConfig.TryGetValue("dynamic_liquidity_threshold_cap", out var dc)
                        ? Convert.ToDouble(dc) : 50_000_000.0;

                    resolvedThreshold = Math.Min(rawThreshold * multiplier, cap);
                    found = true;
                    System.Console.WriteLine($"[INFO] Dynamic liquidity threshold for {stockId}/{date}: " +
                        $"raw={rawThreshold:N0}, multiplier={multiplier}, cap={cap:N0}, resolved={resolvedThreshold:N0}");
                }
            }

            if (!found)
            {
                System.Console.WriteLine($"[WARNING] No dynamic liquidity threshold found for {stockId}/{date}, using fixed massive_matching_amount");
            }

            // Store in EntryConfig so EntryChecker can access it
            EntryConfig["dynamic_liquidity_resolved_threshold"] = resolvedThreshold;
        }

        /// <summary>
        /// Resolves the MA5 bias threshold (MA5 × 1.05) for a specific stock/date.
        /// Loads wide-format parquet (dates as rows, stock IDs as columns) if not cached.
        /// Stores the threshold price in EntryConfig as "ma5_bias5_threshold_price".
        /// </summary>
        private void ResolveMa5BiasThreshold(string stockId, string date)
        {
            var allConfig = Config.GetAllConfig();
            bool enabled = allConfig.TryGetValue("ma5_bias5_enabled", out var e) && e is bool b && b;
            if (!enabled) return;

            // Lazy-load the cache
            if (_ma5BiasCache == null)
            {
                string biasFile = allConfig.TryGetValue("ma5_bias5_file", out var bf)
                    ? bf.ToString() : "ma5_bias5_threshold.parquet";

                // Try CSV first (most reliable), then parquet compat, then parquet original
                string csvFile = biasFile.Replace(".parquet", ".csv");
                string compatFile = biasFile.Replace(".parquet", "_compat.parquet");
                var csvPaths = new[]
                {
                    csvFile,
                    Path.Combine(@"D:\06_資料庫\data", csvFile),
                    Path.Combine(@"D:\03_預估量相關資量\CSharp", csvFile),
                };
                var parquetPaths = new[]
                {
                    compatFile,
                    Path.Combine(@"D:\06_資料庫\data", compatFile),
                    Path.Combine(@"D:\03_預估量相關資量\CSharp", compatFile),
                    biasFile,
                    Path.Combine(@"D:\06_資料庫\data", biasFile),
                    Path.Combine(@"D:\03_預估量相關資量\CSharp", biasFile),
                    Path.Combine(@"D:\feature_data", biasFile),
                };

                // Try CSV paths first
                foreach (var path in csvPaths)
                {
                    if (File.Exists(path))
                    {
                        _ma5BiasCache = ReadMa5BiasCsv(path);
                        System.Console.WriteLine($"[INFO] Loaded MA5 bias thresholds from CSV: {path}");
                        break;
                    }
                }

                // Fall back to parquet if no CSV found
                if (_ma5BiasCache == null)
                {
                    foreach (var path in parquetPaths)
                    {
                        if (File.Exists(path))
                        {
                            _ma5BiasCache = Strategy.ParquetHelper.ReadMa5BiasThresholdParquet(path);
                            if (_ma5BiasCache.Count > 0)
                            {
                                System.Console.WriteLine($"[INFO] Loaded MA5 bias thresholds from: {path}");
                                break;
                            }
                        }
                    }
                }

                _ma5BiasCache ??= new Dictionary<DateTime, Dictionary<string, double>>();

                // Build O(1) date-only lookup
                _ma5BiasDateLookup = new Dictionary<DateTime, DateTime>(_ma5BiasCache.Count);
                foreach (var cachedDate in _ma5BiasCache.Keys)
                {
                    _ma5BiasDateLookup[cachedDate.Date] = cachedDate;
                }

                if (_ma5BiasCache.Count == 0)
                {
                    System.Console.WriteLine($"[WARNING] MA5 bias threshold cache is empty");
                }
            }

            // Look up threshold for this stock/date
            double thresholdPrice = 0;
            bool found = false;

            if (DateTime.TryParse(date, out DateTime targetDate))
            {
                if (_ma5BiasDateLookup != null &&
                    _ma5BiasDateLookup.TryGetValue(targetDate.Date, out DateTime matchedDate) &&
                    _ma5BiasCache.TryGetValue(matchedDate, out var stockThresholds) &&
                    stockThresholds.TryGetValue(stockId, out double rawThreshold))
                {
                    thresholdPrice = rawThreshold;
                    found = true;
                    System.Console.WriteLine($"[INFO] MA5 bias threshold for {stockId}/{date}: {thresholdPrice:F2}");
                }
            }

            if (!found && stockId != "__preload__")
            {
                System.Console.WriteLine($"[WARNING] No MA5 bias threshold found for {stockId}/{date}, filter disabled for this stock");
            }

            EntryConfig["ma5_bias5_threshold_price"] = thresholdPrice;
        }

        /// <summary>
        /// Reads MA5 bias threshold from a wide-format CSV file.
        /// First column is "date", remaining columns are stock IDs with threshold values.
        /// </summary>
        private static Dictionary<DateTime, Dictionary<string, double>> ReadMa5BiasCsv(string path)
        {
            var result = new Dictionary<DateTime, Dictionary<string, double>>();
            try
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length < 2) return result;

                // Parse header
                var header = lines[0].TrimStart('\uFEFF').Split(',');
                // header[0] = "date", header[1..] = stock IDs

                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    var cols = lines[i].Split(',');
                    if (cols.Length < 2) continue;

                    if (!DateTime.TryParse(cols[0].Trim(), out DateTime date)) continue;

                    var stockThresholds = new Dictionary<string, double>();
                    for (int j = 1; j < cols.Length && j < header.Length; j++)
                    {
                        if (double.TryParse(cols[j].Trim(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double val)
                            && val > 0 && !double.IsNaN(val))
                        {
                            stockThresholds[header[j].Trim()] = val;
                        }
                    }

                    if (stockThresholds.Count > 0)
                        result[date] = stockThresholds;
                }

                System.Console.WriteLine($"[INFO] MA5 bias CSV: {result.Count} dates, " +
                    $"{result.Values.SelectMany(d => d.Keys).Distinct().Count()} stocks");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[ERROR] Failed to read MA5 bias CSV: {ex.Message}");
            }
            return result;
        }

        private List<TickData> LoadAndProcessData(string stockId, string date)
        {
            var df = DataProcessor.LoadFeatureData(stockId, date);
            if (df == null || df.Count == 0)
            {
                System.Console.WriteLine($"[ERROR] Cannot load data: {stockId} - {date}");
                return null;
            }

            df = DataProcessor.ProcessTradeData(df);
            if (df == null || df.Count == 0)
            {
                System.Console.WriteLine($"[ERROR] Data empty after processing: {stockId} - {date}");
                return null;
            }

            return df;
        }

        private (double RefPrice, double LimitUpPrice, double LimitDownPrice) GetPriceInfo(
            List<TickData> data, string stockId, string date)
        {
            double? refPrice = DataProcessor.GetReferencePrice(stockId, date);
            if (!refPrice.HasValue || refPrice.Value <= 0)
            {
                refPrice = data.Count > 0 ? data[0].Price : 0;
                System.Console.WriteLine($"[WARNING] Using first price as reference: {refPrice:F2}");
            }

            double limitUpPrice = DataProcessor.CalculateLimitUp(refPrice.Value);
            double limitDownPrice = DataProcessor.CalculateLimitDown(refPrice.Value);

            System.Console.WriteLine($"[INFO] RefPrice: {refPrice:F2}, LimitUp: {limitUpPrice:F2}, LimitDown: {limitDownPrice:F2}");

            return (refPrice.Value, limitUpPrice, limitDownPrice);
        }

        private void GenerateOutputs(
            List<TickData> data, List<TradeRecord> trades,
            string stockId, string date, double refPrice, double limitUpPrice,
            bool skipChart = false)
        {
            // Print trade summary
            if (trades.Count > 0)
            {
                System.Console.WriteLine($"\n--- Trade Summary for {stockId} on {date} ---");
                for (int i = 0; i < trades.Count; i++)
                {
                    var t = trades[i];
                    System.Console.WriteLine(
                        $"  Trade {i + 1}: Entry {t.EntryTime:HH:mm:ss} @ {t.EntryPrice:F2} -> " +
                        $"Exit {t.FinalExitTime:HH:mm:ss} @ {t.FinalExitPrice:F2} | " +
                        $"PnL: {t.PnlPercent:F2}% | Reason: {t.FinalExitReason}");
                }
            }

            // Print entry signal summary
            EntryChecker.PrintEntrySignalsSummary();

            // Export trade details to CSV (always)
            if (trades.Count > 0)
            {
                ExportTradeDetails(trades, stockId, date);
            }

            // Generate interactive HTML chart with entry/exit markers (skip if silent/no_chart)
            if (trades.Count > 0 && !skipChart)
            {
                GenerateChart(data, trades, stockId, date, refPrice, limitUpPrice);
            }
        }

        private void GenerateChart(
            List<TickData> data, List<TradeRecord> trades,
            string stockId, string date, double refPrice, double limitUpPrice)
        {
            try
            {
                var allConfig = Config.GetAllConfig();
                string outputBase = allConfig.TryGetValue("output_path", out var op)
                    ? op.ToString() : Constants.OutputBaseDir;
                string outputDir = Path.Combine(outputBase, date);
                Directory.CreateDirectory(outputDir);

                string htmlPath = Path.Combine(outputDir, $"{stockId}_strategy_chart_{date}.html");
                string pngPath = Path.Combine(outputDir, $"{stockId}_strategy_chart_{date}.png");

                string companyName = DataProcessor.GetCompanyName(stockId);
                string subtitleInfo = BuildThresholdInfo(stockId, date);
                var tradeDicts = trades.Select(t => t.ToDict()).ToList();

                var chartCreator = new Visualization.ChartCreator(allConfig);
                chartCreator.CreateStrategyChart(
                    data, tradeDicts, htmlPath,
                    pngOutputPath: pngPath,
                    refPrice: refPrice,
                    limitUpPrice: limitUpPrice,
                    stockId: stockId,
                    companyName: companyName,
                    subtitleInfo: subtitleInfo);

                System.Console.WriteLine($"[INFO] Strategy chart saved to: {htmlPath}");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[WARNING] Failed to generate chart: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds the threshold info subtitle string for chart display.
        /// Mirrors Python: "大量搓合: 46.00M (動態)" / "大量搓合: 50.0M (固定)" / "大量搓合: 停用"
        /// </summary>
        private string BuildThresholdInfo(string stockId, string date)
        {
            var allConfig = Config.GetAllConfig();

            bool massiveEnabled = allConfig.TryGetValue("massive_matching_enabled", out var me) && me is bool b && b;
            if (!massiveEnabled)
                return "大量搓合: 停用";

            bool useDynamic = allConfig.TryGetValue("use_dynamic_liquidity_threshold", out var ud) && ud is bool bd && bd;
            double fixedAmount = allConfig.TryGetValue("massive_matching_amount", out var ma)
                ? Convert.ToDouble(ma) : 50_000_000.0;

            if (!useDynamic)
                return $"大量搓合: {fixedAmount / 1e6:F1}M (固定)";

            // Dynamic: check if resolved threshold exists
            double resolved = EntryConfig.TryGetValue("dynamic_liquidity_resolved_threshold", out var rt)
                ? Convert.ToDouble(rt) : 0;

            if (resolved > 0)
                return $"大量搓合: {resolved / 1e6:F2}M (動態)";

            return $"大量搓合: {fixedAmount / 1e6:F1}M (固定, 無動態值)";
        }

        private void ExportTradeDetails(List<TradeRecord> trades, string stockId, string date)
        {
            try
            {
                var allConfig = Config.GetAllConfig();
                string outputBase = allConfig.TryGetValue("output_path", out var op)
                    ? op.ToString() : Constants.OutputBaseDir;
                string outputDir = Path.Combine(outputBase, date);
                Directory.CreateDirectory(outputDir);

                string csvPath = Path.Combine(outputDir, $"{stockId}_trade_details_{date}.csv");

                using var writer = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8);
                writer.WriteLine("trade_no,entry_time,entry_price,entry_ratio,day_high_at_entry," +
                    "exit_time,exit_price,exit_reason,pnl_percent,lot_type,group_id");

                for (int i = 0; i < trades.Count; i++)
                {
                    var t = trades[i];
                    writer.WriteLine(
                        $"{i + 1},{t.EntryTime:yyyy-MM-dd HH:mm:ss.fff},{t.EntryPrice:F2}," +
                        $"{t.EntryRatio:F2},{t.DayHighAtEntry:F2}," +
                        $"{t.FinalExitTime:yyyy-MM-dd HH:mm:ss.fff},{t.FinalExitPrice:F2}," +
                        $"\"{t.FinalExitReason}\",{t.PnlPercent:F4},{t.LotType},{t.GroupId}");
                }

                System.Console.WriteLine($"[INFO] Trade details exported to: {csvPath}");

                // Export split entry CSV (when split_entry is enabled)
                if (EntryConfig.TryGetValue("split_entry", out var seObj) &&
                    seObj is Dictionary<string, object> seCfg &&
                    seCfg.TryGetValue("enabled", out var enVal) &&
                    (enVal is bool enabled && enabled))
                {
                    ExportSplitEntryCsv(trades, stockId, date, outputDir);
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[ERROR] Failed to export trade details: {ex.Message}");
            }
        }

        /// <summary>
        /// Exports split entry CSV (Lot A + Lot B combined) for trade_pnl_analyzer compatibility.
        /// Format matches Python: stock_id, entry_time, entry_price, exit_time, exit_price, exit_reason, trade_number, lot_type
        /// </summary>
        private void ExportSplitEntryCsv(List<TradeRecord> trades, string stockId, string date, string outputDir)
        {
            try
            {
                string csvPath = Path.Combine(outputDir, $"{stockId}_split_entry_{date}.csv");
                using var writer = new StreamWriter(csvPath, false, new System.Text.UTF8Encoding(true));
                writer.WriteLine("stock_id,entry_time,entry_price,exit_time,exit_price,exit_reason,trade_number,lot_type");

                foreach (var trade in trades)
                {
                    int groupId = trade.GroupId ?? 0;
                    string lotType = trade.LotType ?? "A";

                    // Trailing stop exits: each batch is a row
                    if (trade.TrailingExitDetails != null && trade.TrailingExitDetails.Count > 0)
                    {
                        foreach (var exitDetail in trade.TrailingExitDetails)
                        {
                            var exitTime = exitDetail.ContainsKey("time") ? exitDetail["time"] : null;
                            var exitPrice = exitDetail.ContainsKey("price") ? exitDetail["price"] : 0;
                            var exitReason = exitDetail.ContainsKey("reason") ? exitDetail["reason"] : "";

                            writer.WriteLine(
                                $"{stockId},{trade.EntryTime:yyyy-MM-dd HH:mm:ss.ffffff},{trade.EntryPrice:F1}," +
                                $"{exitTime:yyyy-MM-dd HH:mm:ss.ffffff},{exitPrice},{exitReason}," +
                                $"{groupId},{lotType}");
                        }

                        // Remaining position (entry price protection / market close)
                        double totalRatio = trade.TrailingExitDetails.Sum(
                            e => e.ContainsKey("ratio") ? Convert.ToDouble(e["ratio"]) : 0.0);
                        if (totalRatio < 1.0 && trade.FinalExitTime.HasValue)
                        {
                            writer.WriteLine(
                                $"{stockId},{trade.EntryTime:yyyy-MM-dd HH:mm:ss.ffffff},{trade.EntryPrice:F1}," +
                                $"{trade.FinalExitTime:yyyy-MM-dd HH:mm:ss.ffffff},{trade.FinalExitPrice}," +
                                $"{trade.FinalExitReason},{groupId},{lotType}");
                        }
                    }
                    // Normal exit
                    else if (trade.FinalExitTime.HasValue)
                    {
                        writer.WriteLine(
                            $"{stockId},{trade.EntryTime:yyyy-MM-dd HH:mm:ss.ffffff},{trade.EntryPrice:F1}," +
                            $"{trade.FinalExitTime:yyyy-MM-dd HH:mm:ss.ffffff},{trade.FinalExitPrice}," +
                            $"{trade.FinalExitReason},{groupId},{lotType}");
                    }
                }

                System.Console.WriteLine($"[INFO] Split entry CSV exported to: {csvPath}");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[WARNING] Failed to export split entry CSV: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculates basic statistics for a list of trades.
        /// </summary>
        private Dictionary<string, object> CalculateStatistics(
            string stockId, List<TradeRecord> trades, string date)
        {
            int entryCount = trades.Count;
            int stopLossCount = trades.Count(t =>
                t.FinalExitReason?.Contains("tick_stop_loss") == true ||
                t.FinalExitReason?.Contains("stop") == true);
            int winCount = trades.Count(t => t.PnlPercent.HasValue && t.PnlPercent.Value > 0);
            double winRate = entryCount > 0 ? (double)winCount / entryCount * 100.0 : 0;
            double totalPnl = trades.Sum(t => t.PnlPercent ?? 0);

            string companyName = DataProcessor.GetCompanyName(stockId);

            return new Dictionary<string, object>
            {
                ["stock_id"] = stockId,
                ["stock_name"] = companyName,
                ["date"] = date,
                ["entry_count"] = entryCount,
                ["stop_loss_count"] = stopLossCount,
                ["win_count"] = winCount,
                ["win_rate"] = winRate,
                ["total_pnl"] = totalPnl,
                ["avg_pnl"] = entryCount > 0 ? totalPnl / entryCount : 0
            };
        }

        /// <summary>
        /// Assigns group_id to Lot A and Lot B trades.
        /// Same group means Lot A and its corresponding Lot B share the same group_id.
        /// Mirrors Python: _assign_trade_groups()
        /// </summary>
        private void AssignTradeGroups(List<TradeRecord> lotATrades, List<TradeRecord> lotBTrades)
        {
            // Assign group_id to Lot A trades
            for (int i = 0; i < lotATrades.Count; i++)
            {
                lotATrades[i].GroupId = i + 1;
                lotATrades[i].LotType = "A";
            }

            // For each Lot B, find the corresponding Lot A (by time matching)
            var splitProcessor = new SplitEntryProcessor(EntryConfig, ExitManager);
            foreach (var tradeB in lotBTrades)
            {
                TradeRecord bestMatch = null;
                foreach (var tradeA in lotATrades)
                {
                    if (tradeA.EntryTime < tradeB.EntryTime)
                    {
                        double timeDiff = (tradeB.EntryTime - tradeA.EntryTime).TotalSeconds;
                        if (timeDiff <= splitProcessor.TimerSeconds)
                        {
                            bestMatch = tradeA;
                        }
                    }
                }
                if (bestMatch != null)
                    tradeB.GroupId = bestMatch.GroupId;
                tradeB.LotType = "B";
            }
        }
    }
}
