using System;
using System.Collections.Generic;
using StrongestVwap.Core.Models;

namespace StrongestVwap.Strategy
{
    /// <summary>
    /// Multi-stock portfolio position tracking.
    /// </summary>
    public class PositionManager
    {
        // Active positions keyed by stockId
        private readonly Dictionary<string, TradeRecord> _positions = new();

        // Completed trades
        private readonly List<TradeRecord> _completedTrades = new();

        public IReadOnlyDictionary<string, TradeRecord> ActivePositions => _positions;
        public IReadOnlyList<TradeRecord> CompletedTrades => _completedTrades;
        public HashSet<string> HeldStockIds => new(_positions.Keys);

        public void OpenPosition(TradeRecord trade)
        {
            _positions[trade.StockId] = trade;
        }

        public bool HasPosition(string stockId)
        {
            return _positions.ContainsKey(stockId);
        }

        public TradeRecord? GetPosition(string stockId)
        {
            return _positions.TryGetValue(stockId, out var trade) ? trade : null;
        }

        /// <summary>
        /// Close a position fully. Sets exit info and moves to completed trades.
        /// </summary>
        public void ClosePosition(string stockId, DateTime exitTime, double exitPrice, string exitReason)
        {
            if (!_positions.TryGetValue(stockId, out var trade))
                return;

            trade.ExitTime = exitTime;
            trade.ExitPrice = exitPrice;
            trade.ExitReason = exitReason;
            trade.IsFullyClosed = true;
            trade.CalculatePnl();

            _completedTrades.Add(trade);
            _positions.Remove(stockId);
        }

        /// <summary>
        /// Force close all remaining positions at market close.
        /// </summary>
        public void ForceCloseAll(DateTime time, Dictionary<string, IndexData> allStocks)
        {
            var stockIds = new List<string>(_positions.Keys);
            foreach (var sid in stockIds)
            {
                double exitPrice = allStocks.TryGetValue(sid, out var idx) ? idx.LastPrice : 0;
                ClosePosition(sid, time, exitPrice, "marketClose");
            }
        }

        public int ActiveCount => _positions.Count;
        public int CompletedCount => _completedTrades.Count;
    }
}
