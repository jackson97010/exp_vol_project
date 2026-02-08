using System;
using System.Collections.Generic;
using System.Linq;
using BacktestModule.Core;

namespace BacktestModule.Analytics
{
    /// <summary>
    /// Trade statistics calculator.
    /// Computes trading statistics such as win rate, average entry/exit prices, and PnL.
    /// </summary>
    public class TradeStatisticsCalculator : ITradeStatisticsCalculator
    {
        private readonly IDataProcessor _dataProcessor;

        /// <summary>
        /// Initializes the statistics calculator.
        /// </summary>
        /// <param name="dataProcessor">Data processor for retrieving company name information. Can be null.</param>
        public TradeStatisticsCalculator(IDataProcessor dataProcessor = null)
        {
            _dataProcessor = dataProcessor;
        }

        /// <summary>
        /// Calculates trading statistics for a given stock.
        /// </summary>
        /// <param name="stockId">Stock code.</param>
        /// <param name="trades">List of trade records.</param>
        /// <param name="date">Trading date string.</param>
        /// <returns>Dictionary containing statistics with Chinese key names for CSV compatibility.</returns>
        public Dictionary<string, object> CalculateStatistics(string stockId, List<TradeRecord> trades, string date)
        {
            // Get company name
            string companyName = _dataProcessor != null
                ? _dataProcessor.GetCompanyName(stockId)
                : stockId;

            // Basic statistics
            int totalTrades = trades.Count;
            int winTrades = 0;
            int lossTrades = 0;
            double totalEntryPrice = 0.0;
            double totalExitPrice = 0.0;
            double totalPnl = 0.0;

            foreach (var trade in trades)
            {
                // Record entry price
                double entryPrice = trade.EntryPrice;
                totalEntryPrice += entryPrice;

                // Calculate exit price
                double exitPrice = CalculateExitPrice(trade);
                totalExitPrice += exitPrice;

                // Calculate single trade PnL (1 lot = 1000 shares per trade)
                double singlePnl = (exitPrice - entryPrice) * 1000;
                totalPnl += singlePnl;

                // Determine win/loss
                if (exitPrice > trade.EntryPrice)
                {
                    winTrades++;
                }
                else if (exitPrice < trade.EntryPrice)
                {
                    lossTrades++;
                }
            }

            // Calculate averages and win rate
            double avgEntryPrice = totalTrades > 0 ? totalEntryPrice / totalTrades : 0;
            double avgExitPrice = totalTrades > 0 ? totalExitPrice / totalTrades : 0;
            double winRate = totalTrades > 0 ? (double)winTrades / totalTrades * 100 : 0;

            return new Dictionary<string, object>
            {
                ["stock_id"] = stockId,
                ["\u80A1\u7968\u540D\u7A31"] = companyName,                    // 股票名稱
                ["\u9032\u5834\u6B21\u6578"] = totalTrades,                      // 進場次數
                ["\u505C\u640D\u6B21\u6578"] = lossTrades,                       // 停損次數
                ["\u52DD\u7387(%)"] = Math.Round(winRate, 2),                    // 勝率(%)
                ["\u9032\u5834\u50F9\u683C"] = Math.Round(avgEntryPrice, 2),     // 進場價格
                ["\u51FA\u5834\u50F9\u683C"] = Math.Round(avgExitPrice, 2),      // 出場價格
                ["\u640D\u76CA"] = Math.Round(totalPnl, 0)                       // 損益
            };
        }

        /// <summary>
        /// Calculates the actual exit price for a trade, considering trailing stops,
        /// two-stage exits, or single exits.
        /// </summary>
        /// <param name="trade">The trade record.</param>
        /// <returns>The weighted average exit price.</returns>
        public double CalculateExitPrice(TradeRecord trade)
        {
            double exitPrice = 0.0;

            if (trade.TrailingExitDetails != null && trade.TrailingExitDetails.Count > 0)
            {
                // Trailing stop exit: calculate weighted average price
                double totalRatio = 0.0;
                double weightedPrice = 0.0;

                foreach (var exit in trade.TrailingExitDetails)
                {
                    double price = exit.ContainsKey("price") ? Convert.ToDouble(exit["price"]) : 0.0;
                    double ratio = exit.ContainsKey("ratio") ? Convert.ToDouble(exit["ratio"]) : 0.0;
                    weightedPrice += price * ratio;
                    totalRatio += ratio;
                }

                // Add final exit (e.g., entry price protection)
                if (trade.FinalExitPrice.HasValue && totalRatio < 1.0)
                {
                    double remainingRatio = 1.0 - totalRatio;
                    weightedPrice += trade.FinalExitPrice.Value * remainingRatio;
                    totalRatio = 1.0;
                }

                exitPrice = totalRatio > 0 ? weightedPrice / totalRatio : trade.EntryPrice;
            }
            else if (trade.PartialExitPrice.HasValue && trade.FinalExitPrice.HasValue)
            {
                // Two-stage exit (original logic)
                exitPrice = trade.PartialExitPrice.Value * 0.5 + trade.FinalExitPrice.Value * 0.5;
            }
            else if (trade.PartialExitPrice.HasValue)
            {
                exitPrice = trade.PartialExitPrice.Value;
            }
            else if (trade.FinalExitPrice.HasValue)
            {
                exitPrice = trade.FinalExitPrice.Value;
            }
            else
            {
                exitPrice = trade.EntryPrice;
            }

            return exitPrice;
        }
    }
}
