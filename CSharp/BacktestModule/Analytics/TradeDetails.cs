using System;
using System.Collections.Generic;
using System.Linq;
using BacktestModule.Core;
using BacktestModule.Strategy;

namespace BacktestModule.Analytics
{
    /// <summary>
    /// Trade details processor.
    /// Converts TradeRecord objects into flat detail dictionaries for CSV export.
    /// Each exit batch gets its own row.
    /// </summary>
    public class TradeDetailsProcessor
    {
        /// <summary>
        /// Collects detailed trade records for a single stock.
        /// Each exit batch is a separate dictionary entry for CSV row output.
        /// </summary>
        /// <param name="trades">List of trade records.</param>
        /// <param name="stockId">Stock code.</param>
        /// <param name="date">Date string.</param>
        /// <returns>List of detail dictionaries (one per exit batch).</returns>
        public List<Dictionary<string, object>> CollectTradeDetails(
            List<TradeRecord> trades, string stockId, string date)
        {
            var tradeDetails = new List<Dictionary<string, object>>();

            for (int i = 0; i < trades.Count; i++)
            {
                var trade = trades[i];
                int tradeNum = i + 1;

                // Calculate actual exit price (weighted average)
                double exitPrice = CalculateActualExitPrice(trade);
                double pnl = (exitPrice - trade.EntryPrice) * 1000; // 1 lot
                double pnlPercent = ((exitPrice - trade.EntryPrice) / trade.EntryPrice) * 100;

                // Handle trailing stop exits (may have multiple batches)
                if (trade.TrailingExitDetails != null && trade.TrailingExitDetails.Count > 0)
                {
                    AddTrailingExitDetails(tradeDetails, trade, tradeNum, stockId, exitPrice, pnl, pnlPercent);
                }
                // Handle two-stage exit mode
                else if (trade.PartialExitTime.HasValue || trade.FinalExitTime.HasValue)
                {
                    AddTwoStageExitDetails(tradeDetails, trade, tradeNum, stockId, exitPrice, pnl, pnlPercent);
                }
            }

            return tradeDetails;
        }

        /// <summary>
        /// Calculates the actual exit price as a weighted average across all exit batches.
        /// </summary>
        /// <param name="trade">The trade record.</param>
        /// <returns>Weighted average exit price.</returns>
        public double CalculateActualExitPrice(TradeRecord trade)
        {
            if (trade.TrailingExitDetails != null && trade.TrailingExitDetails.Count > 0)
            {
                // Trailing stop exit: calculate weighted average price
                double totalRatio = 0.0;
                double weightedPrice = 0.0;

                foreach (var exitDetail in trade.TrailingExitDetails)
                {
                    double price = exitDetail.ContainsKey("price") ? Convert.ToDouble(exitDetail["price"]) : 0.0;
                    double ratio = exitDetail.ContainsKey("ratio") ? Convert.ToDouble(exitDetail["ratio"]) : 0.0;
                    weightedPrice += price * ratio;
                    totalRatio += ratio;
                }

                // If there is remaining position (entry price protection or market close)
                if (trade.FinalExitPrice.HasValue && totalRatio < 1.0)
                {
                    double remainingRatio = 1.0 - totalRatio;
                    weightedPrice += trade.FinalExitPrice.Value * remainingRatio;
                    totalRatio = 1.0;
                }

                return totalRatio > 0 ? weightedPrice / totalRatio : trade.EntryPrice;
            }
            else if (trade.PartialExitPrice.HasValue && trade.FinalExitPrice.HasValue)
            {
                // Two-stage exit
                if (trade.ReentryPrice.HasValue)
                {
                    // With reentry: 50% at partial exit, 50% at final exit
                    return trade.PartialExitPrice.Value * 0.5 + trade.FinalExitPrice.Value * 0.5;
                }
                else
                {
                    // Without reentry: 50% at partial exit, 50% at final exit
                    return trade.PartialExitPrice.Value * 0.5 + trade.FinalExitPrice.Value * 0.5;
                }
            }
            else if (trade.PartialExitPrice.HasValue)
            {
                // Only partial exit
                return trade.PartialExitPrice.Value;
            }
            else if (trade.FinalExitPrice.HasValue)
            {
                // Only final exit
                return trade.FinalExitPrice.Value;
            }
            else
            {
                // No exit (should not happen in theory)
                return trade.EntryPrice;
            }
        }

        /// <summary>
        /// Adds trailing stop exit detail rows.
        /// </summary>
        private void AddTrailingExitDetails(
            List<Dictionary<string, object>> tradeDetails,
            TradeRecord trade, int tradeNum, string stockId,
            double exitPrice, double pnl, double pnlPercent)
        {
            // Each trailing stop exit batch
            for (int j = 0; j < trade.TrailingExitDetails.Count; j++)
            {
                var exitDetail = trade.TrailingExitDetails[j];
                string level = exitDetail.ContainsKey("level") ? exitDetail["level"]?.ToString() : "";
                double price = exitDetail.ContainsKey("price") ? Convert.ToDouble(exitDetail["price"]) : 0.0;
                double ratio = exitDetail.ContainsKey("ratio") ? Convert.ToDouble(exitDetail["ratio"]) : 0.0;
                object time = exitDetail.ContainsKey("time") ? exitDetail["time"] : null;

                double outsideVol3s = trade.EntryOutsideVolume3s > 0
                    ? trade.EntryOutsideVolume3s / 1_000_000.0
                    : 0.0;

                var detail = new Dictionary<string, object>
                {
                    ["\u80A1\u7968\u4EE3\u78BC"] = stockId,                                          // 股票代碼
                    ["\u4EA4\u6613\u7DE8\u865F"] = tradeNum,                                          // 交易編號
                    ["\u9032\u5834\u6642\u9593"] = trade.EntryTime,                                   // 進場時間
                    ["\u9032\u5834\u50F9\u683C"] = trade.EntryPrice,                                  // 進場價格
                    ["\u9032\u5834Ratio"] = trade.EntryRatio,                                         // 進場Ratio
                    ["\u9032\u5834\u5916\u76E43\u79D2(M)"] = outsideVol3s,                            // 進場外盤3秒(M)
                    ["\u51FA\u5834\u6279\u6B21"] = j + 1,                                             // 出場批次
                    ["\u51FA\u5834\u985E\u578B"] = $"\u79FB\u52D5\u505C\u5229_{level}",               // 出場類型: 移動停利_{level}
                    ["\u51FA\u5834\u6642\u9593"] = time,                                              // 出場時間
                    ["\u51FA\u5834\u50F9\u683C"] = price,                                             // 出場價格
                    ["\u51FA\u5834\u6BD4\u4F8B"] = ratio * 100,                                       // 出場比例
                    ["\u51FA\u5834\u539F\u56E0"] = $"\u8DCC\u7834{level}\u4F4E\u9EDE",                // 出場原因: 跌破{level}低點
                    ["\u5BE6\u969B\u51FA\u5834\u5747\u50F9"] = exitPrice,                             // 實際出場均價
                    ["\u7E3D\u640D\u76CA\u91D1\u984D"] = pnl,                                        // 總損益金額
                    ["\u7E3D\u640D\u76CA\u767E\u5206\u6BD4"] = pnlPercent                            // 總損益百分比
                };
                tradeDetails.Add(detail);
            }

            // If there is entry price protection or market close exit (remaining position)
            if (trade.FinalExitTime.HasValue && !string.IsNullOrEmpty(trade.FinalExitReason))
            {
                double remainingRatio = 1.0 - trade.TrailingExitDetails.Sum(
                    e => e.ContainsKey("ratio") ? Convert.ToDouble(e["ratio"]) : 0.0);

                if (remainingRatio > 0)
                {
                    double outsideVol3s = trade.EntryOutsideVolume3s > 0
                        ? trade.EntryOutsideVolume3s / 1_000_000.0
                        : 0.0;

                    var detail = new Dictionary<string, object>
                    {
                        ["\u80A1\u7968\u4EE3\u78BC"] = stockId,
                        ["\u4EA4\u6613\u7DE8\u865F"] = tradeNum,
                        ["\u9032\u5834\u6642\u9593"] = trade.EntryTime,
                        ["\u9032\u5834\u50F9\u683C"] = trade.EntryPrice,
                        ["\u9032\u5834Ratio"] = trade.EntryRatio,
                        ["\u9032\u5834\u5916\u76E43\u79D2(M)"] = outsideVol3s,
                        ["\u51FA\u5834\u6279\u6B21"] = trade.TrailingExitDetails.Count + 1,            // 出場批次
                        ["\u51FA\u5834\u985E\u578B"] = "\u6700\u7D42\u6E05\u5009",                    // 出場類型: 最終清倉
                        ["\u51FA\u5834\u6642\u9593"] = trade.FinalExitTime,
                        ["\u51FA\u5834\u50F9\u683C"] = trade.FinalExitPrice,
                        ["\u51FA\u5834\u6BD4\u4F8B"] = remainingRatio * 100,
                        ["\u51FA\u5834\u539F\u56E0"] = trade.FinalExitReason,
                        ["\u5BE6\u969B\u51FA\u5834\u5747\u50F9"] = exitPrice,
                        ["\u7E3D\u640D\u76CA\u91D1\u984D"] = pnl,
                        ["\u7E3D\u640D\u76CA\u767E\u5206\u6BD4"] = pnlPercent
                    };
                    tradeDetails.Add(detail);
                }
            }
        }

        /// <summary>
        /// Adds two-stage exit detail rows (partial exit + optional reentry + final exit).
        /// </summary>
        private void AddTwoStageExitDetails(
            List<Dictionary<string, object>> tradeDetails,
            TradeRecord trade, int tradeNum, string stockId,
            double exitPrice, double pnl, double pnlPercent)
        {
            int batch = 1;
            double outsideVol3s = trade.EntryOutsideVolume3s > 0
                ? trade.EntryOutsideVolume3s / 1_000_000.0
                : 0.0;

            // Stage 1: Partial exit (reduce 50%)
            if (trade.PartialExitTime.HasValue)
            {
                var detail = new Dictionary<string, object>
                {
                    ["\u80A1\u7968\u4EE3\u78BC"] = stockId,
                    ["\u4EA4\u6613\u7DE8\u865F"] = tradeNum,
                    ["\u9032\u5834\u6642\u9593"] = trade.EntryTime,
                    ["\u9032\u5834\u50F9\u683C"] = trade.EntryPrice,
                    ["\u9032\u5834Ratio"] = trade.EntryRatio,
                    ["\u9032\u5834\u5916\u76E43\u79D2(M)"] = outsideVol3s,
                    ["\u51FA\u5834\u6279\u6B21"] = batch,                                              // 出場批次
                    ["\u51FA\u5834\u985E\u578B"] = "\u6E1B\u78BC50%",                                  // 出場類型: 減碼50%
                    ["\u51FA\u5834\u6642\u9593"] = trade.PartialExitTime,
                    ["\u51FA\u5834\u50F9\u683C"] = trade.PartialExitPrice,
                    ["\u51FA\u5834\u6BD4\u4F8B"] = 50.0,
                    ["\u51FA\u5834\u539F\u56E0"] = trade.PartialExitReason ?? "",
                    ["\u5BE6\u969B\u51FA\u5834\u5747\u50F9"] = exitPrice,
                    ["\u7E3D\u640D\u76CA\u91D1\u984D"] = pnl,
                    ["\u7E3D\u640D\u76CA\u767E\u5206\u6BD4"] = pnlPercent
                };
                tradeDetails.Add(detail);
                batch++;
            }

            // Reentry (if any)
            if (trade.ReentryTime.HasValue)
            {
                var detail = new Dictionary<string, object>
                {
                    ["\u80A1\u7968\u4EE3\u78BC"] = stockId,
                    ["\u4EA4\u6613\u7DE8\u865F"] = tradeNum,
                    ["\u9032\u5834\u6642\u9593"] = trade.EntryTime,
                    ["\u9032\u5834\u50F9\u683C"] = trade.EntryPrice,
                    ["\u9032\u5834Ratio"] = trade.EntryRatio,
                    ["\u9032\u5834\u5916\u76E43\u79D2(M)"] = outsideVol3s,
                    ["\u51FA\u5834\u6279\u6B21"] = (object)"\u56DE\u88DC",                             // 出場批次: 回補
                    ["\u51FA\u5834\u985E\u578B"] = "\u56DE\u88DC\u9032\u5834",                         // 出場類型: 回補進場
                    ["\u51FA\u5834\u6642\u9593"] = trade.ReentryTime,
                    ["\u51FA\u5834\u50F9\u683C"] = trade.ReentryPrice,
                    ["\u51FA\u5834\u6BD4\u4F8B"] = (object)"-",
                    ["\u51FA\u5834\u539F\u56E0"] = "\u50F9\u683C\u5275\u65B0\u9AD8\u4E14\u5916\u76E4\u589E\u52A0",  // 價格創新高且外盤增加
                    ["\u5BE6\u969B\u51FA\u5834\u5747\u50F9"] = exitPrice,
                    ["\u7E3D\u640D\u76CA\u91D1\u984D"] = pnl,
                    ["\u7E3D\u640D\u76CA\u767E\u5206\u6BD4"] = pnlPercent
                };
                tradeDetails.Add(detail);
            }

            // Final exit (close all)
            if (trade.FinalExitTime.HasValue)
            {
                string exitType = trade.ReentryTime.HasValue
                    ? "\u56DE\u88DC\u5F8C\u6E05\u5009"    // 回補後清倉
                    : "\u6E05\u5009";                       // 清倉
                double exitRatio = trade.PartialExitTime.HasValue ? 50.0 : 100.0;

                var detail = new Dictionary<string, object>
                {
                    ["\u80A1\u7968\u4EE3\u78BC"] = stockId,
                    ["\u4EA4\u6613\u7DE8\u865F"] = tradeNum,
                    ["\u9032\u5834\u6642\u9593"] = trade.EntryTime,
                    ["\u9032\u5834\u50F9\u683C"] = trade.EntryPrice,
                    ["\u9032\u5834Ratio"] = trade.EntryRatio,
                    ["\u9032\u5834\u5916\u76E43\u79D2(M)"] = outsideVol3s,
                    ["\u51FA\u5834\u6279\u6B21"] = batch,
                    ["\u51FA\u5834\u985E\u578B"] = exitType,
                    ["\u51FA\u5834\u6642\u9593"] = trade.FinalExitTime,
                    ["\u51FA\u5834\u50F9\u683C"] = trade.FinalExitPrice,
                    ["\u51FA\u5834\u6BD4\u4F8B"] = exitRatio,
                    ["\u51FA\u5834\u539F\u56E0"] = trade.FinalExitReason ?? "",
                    ["\u5BE6\u969B\u51FA\u5834\u5747\u50F9"] = exitPrice,
                    ["\u7E3D\u640D\u76CA\u91D1\u984D"] = pnl,
                    ["\u7E3D\u640D\u76CA\u767E\u5206\u6BD4"] = pnlPercent
                };
                tradeDetails.Add(detail);
            }
        }
    }
}
