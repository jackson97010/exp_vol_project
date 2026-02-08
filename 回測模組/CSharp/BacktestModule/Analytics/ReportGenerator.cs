using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using BacktestModule.Core;

namespace BacktestModule.Analytics
{
    /// <summary>
    /// Report generator.
    /// Produces backtest reports and analysis results to the log output.
    /// </summary>
    public class ReportGenerator : IReportGenerator
    {
        private readonly ILogger<ReportGenerator> _logger;
        private readonly IEntryChecker _entryChecker;

        /// <summary>
        /// Initializes the report generator.
        /// </summary>
        /// <param name="entryChecker">Entry checker for retrieving entry signal information. Can be null.</param>
        /// <param name="logger">Logger instance. Can be null (will use NullLogger).</param>
        public ReportGenerator(IEntryChecker entryChecker = null, ILogger<ReportGenerator> logger = null)
        {
            _entryChecker = entryChecker;
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ReportGenerator>.Instance;
        }

        /// <summary>
        /// Generates a backtest report for a single stock, logging trade details and entry signal statistics.
        /// </summary>
        /// <param name="data">Tick data list.</param>
        /// <param name="trades">List of completed trade records.</param>
        /// <param name="stockId">Stock code.</param>
        /// <param name="date">Trading date string.</param>
        public void GenerateReport(List<TickData> data, List<TradeRecord> trades, string stockId, string date)
        {
            _logger.LogInformation("");
            _logger.LogInformation(new string('=', 60));
            _logger.LogInformation("Backtest Report - {StockId} ({Date})", stockId, date);
            _logger.LogInformation(new string('=', 60));

            // Trade statistics
            _logger.LogInformation("");
            _logger.LogInformation("Trade Statistics:");
            _logger.LogInformation("  Total trades: {TradeCount}", trades.Count);

            // Detailed trade records
            LogTradeDetails(trades);

            // Entry signal statistics
            LogEntrySignals();
        }

        /// <summary>
        /// Logs detailed trade records including entries, partial exits, reentries, trailing stops, and final exits.
        /// </summary>
        /// <param name="trades">List of trade records.</param>
        private void LogTradeDetails(List<TradeRecord> trades)
        {
            for (int i = 0; i < trades.Count; i++)
            {
                var trade = trades[i];
                _logger.LogInformation("");
                _logger.LogInformation("Trade #{TradeNum}:", i + 1);
                _logger.LogInformation("  Entry: {EntryTime} @ {EntryPrice:F2} (Ratio: {EntryRatio:F1})",
                    trade.EntryTime, trade.EntryPrice, trade.EntryRatio);

                if (trade.PartialExitTime.HasValue)
                {
                    _logger.LogInformation("  Partial exit: {PartialExitTime} @ {PartialExitPrice:F2}",
                        trade.PartialExitTime, trade.PartialExitPrice);
                }

                if (trade.ReentryTime.HasValue)
                {
                    _logger.LogInformation("  Reentry: {ReentryTime} @ {ReentryPrice:F2}",
                        trade.ReentryTime, trade.ReentryPrice);
                }

                // Handle trailing stop exits
                if (trade.TrailingExitDetails != null && trade.TrailingExitDetails.Count > 0)
                {
                    foreach (var exit in trade.TrailingExitDetails)
                    {
                        double price = exit.ContainsKey("price") ? Convert.ToDouble(exit["price"]) : 0.0;
                        double ratio = exit.ContainsKey("ratio") ? Convert.ToDouble(exit["ratio"]) : 0.0;
                        object time = exit.ContainsKey("time") ? exit["time"] : null;

                        _logger.LogInformation("  Trailing stop: {Time} @ {Price:F2} ({Ratio:F0}%)",
                            time, price, ratio * 100);
                    }
                }

                if (trade.FinalExitTime.HasValue)
                {
                    _logger.LogInformation("  Close all: {FinalExitTime} @ {FinalExitPrice:F2}",
                        trade.FinalExitTime, trade.FinalExitPrice);
                }

                if (trade.PnlPercent.HasValue)
                {
                    _logger.LogInformation("  PnL: {PnlPercent:F2}%", trade.PnlPercent);
                }
            }
        }

        /// <summary>
        /// Logs entry signal statistics from the entry checker.
        /// </summary>
        private void LogEntrySignals()
        {
            if (_entryChecker == null)
                return;

            var signals = _entryChecker.GetEntrySignals();
            if (signals == null || signals.Count == 0)
                return;

            _logger.LogInformation("");
            _logger.LogInformation("Entry Signal Analysis:");
            _logger.LogInformation("  Total entry signals: {SignalCount}", signals.Count);

            int passedCount = signals.Count(s => s.Passed);
            double passedPercentage = signals.Count > 0 ? (double)passedCount / signals.Count * 100 : 0;
            _logger.LogInformation("  Passed signals: {Passed}/{Total} ({Percentage:F1}%)",
                passedCount, signals.Count, passedPercentage);

            // Show condition names from the first signal
            if (signals.Count > 0 && signals[0].Conditions != null && signals[0].Conditions.Count > 0)
            {
                _logger.LogInformation("");
                _logger.LogInformation("Condition Statistics:");
                foreach (var conditionName in signals[0].Conditions.Keys)
                {
                    _logger.LogInformation("  {ConditionName}", conditionName);
                }
            }
        }

        /// <summary>
        /// Generates a batch backtest summary report.
        /// </summary>
        /// <param name="results">List of per-stock statistics dictionaries.</param>
        /// <param name="date">Backtest date string.</param>
        /// <returns>The same results list (for chaining or further processing). Returns empty list if no results.</returns>
        public List<Dictionary<string, object>> GenerateSummaryReport(
            List<Dictionary<string, object>> results, string date)
        {
            if (results == null || results.Count == 0)
            {
                _logger.LogWarning("No backtest results available for report generation.");
                return new List<Dictionary<string, object>>();
            }

            // Calculate totals
            int totalTrades = results.Sum(r =>
                r.ContainsKey("\u9032\u5834\u6B21\u6578") ? Convert.ToInt32(r["\u9032\u5834\u6B21\u6578"]) : 0);

            double totalPnl = results.Sum(r =>
                r.ContainsKey("\u640D\u76CA") ? Convert.ToDouble(r["\u640D\u76CA"]) : 0);

            var withTrades = results
                .Where(r => r.ContainsKey("\u9032\u5834\u6B21\u6578") && Convert.ToInt32(r["\u9032\u5834\u6B21\u6578"]) > 0)
                .ToList();

            double avgWinRate = withTrades.Count > 0
                ? withTrades.Average(r =>
                    r.ContainsKey("\u52DD\u7387(%)") ? Convert.ToDouble(r["\u52DD\u7387(%)"]) : 0)
                : 0;

            _logger.LogInformation("");
            _logger.LogInformation(new string('=', 60));
            _logger.LogInformation("Batch Backtest Summary - {Date}", date);
            _logger.LogInformation(new string('=', 60));
            _logger.LogInformation("Total stocks: {StockCount}", results.Count);
            _logger.LogInformation("Total trades: {TotalTrades}", totalTrades);
            _logger.LogInformation("Total PnL: {TotalPnl:N0}", totalPnl);
            _logger.LogInformation("Average win rate: {AvgWinRate:F2}%", avgWinRate);

            return results;
        }
    }
}
