using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public MassiveMatchingTracker MassiveMatchingTracker { get; }
        public InsideOutsideRatioTracker InsideOutsideRatioTracker { get; }
        public InsideOutsideRatioTracker LargeOrderIoRatioTracker { get; }

        // Entry signal log
        public List<Dictionary<string, object>> EntrySignalLog { get; } = new();

        // Last tick data (for chart generation)
        public List<TickData> LastTickData { get; private set; }

        public BacktestEngine(string configPath = "Bo_v2.yaml")
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
            MassiveMatchingTracker = new MassiveMatchingTracker(windowSeconds: Constants.MassiveMatchingWindow);
            InsideOutsideRatioTracker = new InsideOutsideRatioTracker(windowSeconds: Constants.IoRatioWindow);
            LargeOrderIoRatioTracker = new InsideOutsideRatioTracker(
                windowSeconds: Constants.IoRatioWindow,
                minVolumeThreshold: Constants.LargeOrderThreshold);
        }

        /// <summary>
        /// Run backtest for a single stock on a single date.
        /// </summary>
        public List<TradeRecord> RunSingleBacktest(string stockId, string date, bool silent = false)
        {
            // Reset position manager state
            PositionManager.Reset();

            Console.WriteLine($"\n{"",60}");
            Console.WriteLine($"Starting backtest: {stockId} - {date}");
            Console.WriteLine($"{"",60}");

            // 1. Load and process data
            var df = LoadAndProcessData(stockId, date);
            if (df == null || df.Count == 0)
                return new List<TradeRecord>();

            // 2. Get price info
            var (refPrice, limitUpPrice, limitDownPrice) = GetPriceInfo(df, stockId, date);

            // 3. Run backtest loop
            var backtestLoop = new BacktestLoop(this);
            var trades = backtestLoop.Run(df, stockId, refPrice, limitUpPrice);

            // 4. Generate outputs (non-silent mode)
            if (!silent)
            {
                GenerateOutputs(df, trades, stockId, date, refPrice, limitUpPrice);
            }

            // Save tick data for chart use
            LastTickData = df;

            return trades;
        }

        /// <summary>
        /// Run batch backtest for multiple stocks.
        /// </summary>
        public List<Dictionary<string, object>> RunBatchBacktest(
            List<string> stockList, string date,
            bool outputCsv = true, bool createCharts = true)
        {
            var results = new List<Dictionary<string, object>>();
            var allTradeDetails = new List<Dictionary<string, object>>();

            foreach (var stockId in stockList)
            {
                Console.WriteLine($"\nProcessing stock: {stockId}");

                var trades = RunSingleBacktest(stockId, date, silent: !createCharts);

                if (trades.Count > 0)
                {
                    // Calculate statistics
                    var stats = CalculateStatistics(stockId, trades, date);
                    results.Add(stats);
                }
            }

            // Summary
            if (results.Count > 0)
            {
                Console.WriteLine($"\nBatch backtest complete. {results.Count} stocks had trades.");
            }

            return results;
        }

        private List<TickData> LoadAndProcessData(string stockId, string date)
        {
            var df = DataProcessor.LoadFeatureData(stockId, date);
            if (df == null || df.Count == 0)
            {
                Console.WriteLine($"[ERROR] Cannot load data: {stockId} - {date}");
                return null;
            }

            df = DataProcessor.ProcessTradeData(df);
            if (df == null || df.Count == 0)
            {
                Console.WriteLine($"[ERROR] Data empty after processing: {stockId} - {date}");
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
                Console.WriteLine($"[WARNING] Using first price as reference: {refPrice:F2}");
            }

            double limitUpPrice = DataProcessor.CalculateLimitUp(refPrice.Value);
            double limitDownPrice = DataProcessor.CalculateLimitDown(refPrice.Value);

            Console.WriteLine($"[INFO] RefPrice: {refPrice:F2}, LimitUp: {limitUpPrice:F2}, LimitDown: {limitDownPrice:F2}");

            return (refPrice.Value, limitUpPrice, limitDownPrice);
        }

        private void GenerateOutputs(
            List<TickData> data, List<TradeRecord> trades,
            string stockId, string date, double refPrice, double limitUpPrice)
        {
            // Print trade summary
            if (trades.Count > 0)
            {
                Console.WriteLine($"\n--- Trade Summary for {stockId} on {date} ---");
                for (int i = 0; i < trades.Count; i++)
                {
                    var t = trades[i];
                    Console.WriteLine(
                        $"  Trade {i + 1}: Entry {t.EntryTime:HH:mm:ss} @ {t.EntryPrice:F2} -> " +
                        $"Exit {t.FinalExitTime:HH:mm:ss} @ {t.FinalExitPrice:F2} | " +
                        $"PnL: {t.PnlPercent:F2}% | Reason: {t.FinalExitReason}");
                }
            }

            // Print entry signal summary
            EntryChecker.PrintEntrySignalsSummary();

            // Export trade details to CSV
            if (trades.Count > 0)
            {
                ExportTradeDetails(trades, stockId, date);
            }
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
                    "exit_time,exit_price,exit_reason,pnl_percent");

                for (int i = 0; i < trades.Count; i++)
                {
                    var t = trades[i];
                    writer.WriteLine(
                        $"{i + 1},{t.EntryTime:yyyy-MM-dd HH:mm:ss.fff},{t.EntryPrice:F2}," +
                        $"{t.EntryRatio:F2},{t.DayHighAtEntry:F2}," +
                        $"{t.FinalExitTime:yyyy-MM-dd HH:mm:ss.fff},{t.FinalExitPrice:F2}," +
                        $"\"{t.FinalExitReason}\",{t.PnlPercent:F4}");
                }

                Console.WriteLine($"[INFO] Trade details exported to: {csvPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to export trade details: {ex.Message}");
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
    }
}
