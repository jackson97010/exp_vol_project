using System;
using BacktestModule.Strategy;
using BacktestModule.Core.Models;
using System.Collections.Generic;
using BacktestModule.Strategy;
using BacktestModule.Core.Models;

namespace BacktestModule.Core
{
    /// <summary>
    /// Interface for data processor. Implemented by Teammate 2 in BoReentryBacktest.Strategy.
    /// Stub provided here so Analytics/Exporters/Visualization modules can compile.
    /// </summary>
    public interface IDataProcessor
    {
        string GetCompanyName(string stockId);
    }

    /// <summary>
    /// Interface for entry checker. Implemented by Teammate 2 in BoReentryBacktest.Strategy.
    /// Stub provided here so ReportGenerator can compile.
    /// </summary>
    public interface IEntryChecker
    {
        /// <summary>
        /// Returns the list of entry signals recorded during the backtest.
        /// </summary>
        List<EntrySignal> GetEntrySignals();
    }

    /// <summary>
    /// Interface for trade statistics calculator.
    /// </summary>
    public interface ITradeStatisticsCalculator
    {
        Dictionary<string, object> CalculateStatistics(string stockId, List<TradeRecord> trades, string date);
    }

    /// <summary>
    /// Interface for report generator.
    /// </summary>
    public interface IReportGenerator
    {
        void GenerateReport(List<TickData> data, List<TradeRecord> trades, string stockId, string date);
        List<Dictionary<string, object>> GenerateSummaryReport(List<Dictionary<string, object>> results, string date);
    }

    /// <summary>
    /// Interface for CSV exporter.
    /// </summary>
    public interface ICsvExporter
    {
        void ExportSummaryToCsv(List<Dictionary<string, object>> results, string date);
        void ExportDetailedTradesToCsv(List<Dictionary<string, object>> allTradeDetails, string date);
    }

    /// <summary>
    /// Interface for chart creator.
    /// </summary>
    public interface IChartCreator
    {
        string CreateStrategyChart(
            List<TickData> data,
            List<Dictionary<string, object>> trades,
            string outputPath,
            string pngOutputPath = null,
            double? refPrice = null,
            double? limitUpPrice = null,
            string stockId = null,
            string companyName = null,
            string subtitleInfo = null);
    }
}
