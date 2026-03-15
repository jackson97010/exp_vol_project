using System;
using System.Collections.Generic;
using System.Linq;
using BacktestModule.Core.Models;

namespace BacktestModule.Strategy
{
    /// <summary>
    /// Split entry post-processor (Lot B).
    /// After Lot A backtest completes, scans tick data for Lot B entry opportunities.
    /// Lot B = dayhigh - N ticks (回檔進場), with a time window from Lot A entry.
    /// Exit logic reuses ExitManager methods with Lot A entry price as reference.
    /// Mirrors Python: strategy_modules/split_entry_processor.py
    /// </summary>
    public class SplitEntryProcessor
    {
        public bool Enabled { get; }
        public int TickOffset { get; }
        public double TimerSeconds { get; }
        public string ExitReference { get; }

        private readonly ExitManager _exitManager;

        public SplitEntryProcessor(Dictionary<string, object> entryConfig, ExitManager exitManager)
        {
            _exitManager = exitManager;

            if (entryConfig.TryGetValue("split_entry", out var splitObj) &&
                splitObj is Dictionary<string, object> splitCfg)
            {
                Enabled = GetBool(splitCfg, "enabled", false);
                TickOffset = GetInt(splitCfg, "lot_b_tick_offset", -1);
                TimerSeconds = GetDouble(splitCfg, "lot_b_timer_seconds", 10.0);
                ExitReference = GetString(splitCfg, "lot_b_exit_reference", "lot_a");
            }
            else
            {
                Enabled = false;
                TickOffset = -1;
                TimerSeconds = 10.0;
                ExitReference = "lot_a";
            }
        }

        /// <summary>
        /// Process all Lot A trades, scanning for Lot B entry opportunities.
        /// </summary>
        public List<TradeRecord> Process(
            List<TickData> df,
            List<TradeRecord> lotATrades,
            string stockId,
            double limitUpPrice)
        {
            if (!Enabled || lotATrades == null || lotATrades.Count == 0)
                return new List<TradeRecord>();

            var lotBTrades = new List<TradeRecord>();

            foreach (var lotA in lotATrades)
            {
                var lotB = FindAndSimulateLotB(df, lotA, limitUpPrice);
                if (lotB != null)
                    lotBTrades.Add(lotB);
            }

            if (lotBTrades.Count > 0)
            {
                System.Console.WriteLine(
                    $"[分批進場] {stockId}: 找到 {lotBTrades.Count} 筆 Lot B 進場");
            }

            return lotBTrades;
        }

        private TradeRecord FindAndSimulateLotB(
            List<TickData> df,
            TradeRecord lotA,
            double limitUpPrice)
        {
            // Calculate Lot B target entry price
            double dayHigh = lotA.DayHighAtEntry;
            double targetPrice = TickSizeHelper.AddTicks(dayHigh, TickOffset);

            // Time window
            DateTime timerStart = lotA.EntryTime;
            DateTime timerEnd = timerStart.AddSeconds(TimerSeconds);

            // Scan tick data for entry opportunity
            // Find: timerStart < time <= timerEnd && price > 0 && price == targetPrice
            TickData entryTick = null;
            foreach (var row in df)
            {
                if (row.Time <= timerStart) continue;
                if (row.Time > timerEnd) break;
                if (row.Price <= 0) continue;

                if (Math.Abs(row.Price - targetPrice) < 1e-6)
                {
                    entryTick = row;
                    break;
                }
            }

            if (entryTick == null)
                return null;

            DateTime lotBEntryTime = entryTick.Time;
            double lotBEntryPrice = entryTick.Price;

            System.Console.WriteLine(
                $"[Lot B 進場] 時間: {lotBEntryTime}, " +
                $"價格: {lotBEntryPrice:F2} " +
                $"(目標: day_high {dayHigh:F2} + {TickOffset} ticks), " +
                $"對應 Lot A 進場: {lotA.EntryTime}");

            // Simulate exit
            return SimulateLotBExit(df, lotA, lotBEntryTime, lotBEntryPrice, limitUpPrice);
        }

        private TradeRecord SimulateLotBExit(
            List<TickData> df,
            TradeRecord lotA,
            DateTime entryTime,
            double entryPrice,
            double limitUpPrice)
        {
            // Create independent PositionManager
            var pm = new PositionManager();

            // Exit reference price
            double refEntryPrice = ExitReference == "lot_a" ? lotA.EntryPrice : entryPrice;

            // Open position using reference price for exit calculations
            var position = pm.OpenPosition(
                entryTime: entryTime,
                entryPrice: refEntryPrice,
                entryBidThickness: 0.0,
                dayHighAtEntry: lotA.DayHighAtEntry,
                entryRatio: lotA.EntryRatio,
                entryOutsideVolume3s: 0.0,
                entryOutsideVolume5s: 0.0);

            // Scan ticks after entry
            DateTime? exitTime = null;
            double exitPrice = 0;
            string exitReason = null;

            // Find start index (binary search would be faster but linear is fine for single-stock)
            int startIdx = 0;
            for (int i = 0; i < df.Count; i++)
            {
                if (df[i].Time > entryTime)
                {
                    startIdx = i;
                    break;
                }
            }

            for (int i = startIdx; i < df.Count; i++)
            {
                var row = df[i];
                if (row.Price <= 0) continue;

                DateTime currentTime = row.Time;
                double currentPrice = row.Price;

                // Update highest price
                if (currentPrice > position.HighestPrice)
                    position.HighestPrice = currentPrice;

                // 1. Limit up exit
                if (currentPrice >= limitUpPrice)
                {
                    exitTime = currentTime;
                    exitPrice = limitUpPrice;
                    exitReason = "漲停價出場";
                    break;
                }

                // 2. Trailing stop (if enabled)
                if (_exitManager.TrailingStopEnabled)
                {
                    var trailingResult = _exitManager.CheckTrailingStop(position, row, currentTime, currentPrice);
                    if (trailingResult != null)
                    {
                        pm.TrailingStopExit(
                            exitTime: trailingResult.ExitTime,
                            exitPrice: trailingResult.ExitPrice,
                            exitRatio: trailingResult.ExitRatio,
                            exitLevel: trailingResult.ExitLevel ?? "",
                            exitReason: trailingResult.ExitReason ?? "");

                        if (position.RemainingRatio <= 0)
                        {
                            exitTime = trailingResult.ExitTime;
                            exitPrice = trailingResult.ExitPrice;
                            exitReason = "移動停利全出";
                            break;
                        }
                        continue;
                    }
                }

                // 3. Entry price protection (after partial exit)
                if (position.RemainingRatio < 1.0)
                {
                    var protResult = _exitManager.CheckEntryPriceProtection(position, currentPrice, currentTime);
                    if (protResult != null)
                    {
                        exitTime = currentTime;
                        exitPrice = currentPrice;
                        exitReason = protResult.ExitReason ?? "進場價保護";
                        break;
                    }
                }

                // 4. Hard stop loss
                var stopResult = _exitManager.CheckHardStopLoss(position, currentPrice, currentTime);
                if (stopResult != null)
                {
                    exitTime = currentTime;
                    exitPrice = currentPrice;
                    exitReason = "硬停損";
                    break;
                }
            }

            // 5. Market close (no exit triggered)
            if (exitTime == null && df.Count > 0)
            {
                var lastRow = df[df.Count - 1];
                exitTime = lastRow.Time;
                exitPrice = lastRow.Price > 0 ? lastRow.Price : entryPrice;
                exitReason = "收盤清倉";
            }

            // Close position
            pm.ClosePosition(
                exitTime: exitTime ?? DateTime.Now,
                exitPrice: exitPrice,
                exitReason: exitReason ?? "Unknown");

            // Get trade record and fix prices/PnL for Lot B
            if (pm.TradeHistory.Count > 0)
            {
                var trade = pm.TradeHistory[0];

                // Restore actual Lot B entry price (was using ref price for exit calc)
                trade.EntryPrice = entryPrice;

                // Recalculate PnL with actual entry price
                if (trade.TrailingExitDetails != null && trade.TrailingExitDetails.Count > 0)
                {
                    double totalPnl = 0.0;
                    foreach (var exitDetail in trade.TrailingExitDetails)
                    {
                        double ep = Convert.ToDouble(exitDetail["price"]);
                        double er = Convert.ToDouble(exitDetail["ratio"]);
                        totalPnl += (ep - entryPrice) / entryPrice * 100.0 * er;
                    }
                    double remaining = 1.0 - trade.TrailingExitDetails.Sum(
                        e => Convert.ToDouble(e["ratio"]));
                    if (remaining > 0 && trade.FinalExitPrice.HasValue)
                    {
                        totalPnl += (trade.FinalExitPrice.Value - entryPrice) / entryPrice * 100.0 * remaining;
                    }
                    trade.PnlPercent = totalPnl;
                }
                else if (trade.FinalExitPrice.HasValue)
                {
                    trade.PnlPercent = (trade.FinalExitPrice.Value - entryPrice) / entryPrice * 100.0;
                }

                // Mark as Lot B
                trade.LotType = "B";

                System.Console.WriteLine(
                    $"[Lot B 出場] 時間: {exitTime}, 價格: {exitPrice:F2}, " +
                    $"原因: {exitReason}, PnL: {trade.PnlPercent:F2}%");

                return trade;
            }

            return null;
        }

        // Helper methods
        private static bool GetBool(Dictionary<string, object> d, string key, bool def)
        {
            if (d.TryGetValue(key, out var v) && v != null)
            {
                if (v is bool bv) return bv;
                string s = v.ToString()?.Trim().ToLowerInvariant() ?? "";
                if (s == "true" || s == "yes" || s == "1") return true;
                if (s == "false" || s == "no" || s == "0") return false;
            }
            return def;
        }

        private static int GetInt(Dictionary<string, object> d, string key, int def)
        {
            if (d.TryGetValue(key, out var v) && v != null)
            {
                if (v is int iv) return iv;
                if (v is long lv) return (int)lv;
                if (v is double dv) return (int)dv;
                if (int.TryParse(v.ToString(), out var p)) return p;
            }
            return def;
        }

        private static double GetDouble(Dictionary<string, object> d, string key, double def)
        {
            if (d.TryGetValue(key, out var v) && v != null)
            {
                if (v is double dv) return dv;
                if (v is int iv) return iv;
                if (v is long lv) return lv;
                if (double.TryParse(v.ToString(), out var p)) return p;
            }
            return def;
        }

        private static string GetString(Dictionary<string, object> d, string key, string def)
        {
            if (d.TryGetValue(key, out var v) && v != null)
                return v.ToString() ?? def;
            return def;
        }
    }
}
