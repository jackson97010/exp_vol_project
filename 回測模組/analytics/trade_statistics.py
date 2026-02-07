"""
交易統計計算模組
負責計算交易相關的統計數據
"""
from typing import Dict, List
from strategy_modules.position_manager import TradeRecord


class TradeStatisticsCalculator:
    """交易統計計算器"""

    def __init__(self, data_processor=None):
        """
        初始化統計計算器

        Args:
            data_processor: 資料處理器，用於取得公司名稱等資訊
        """
        self.data_processor = data_processor

    def calculate_statistics(self, stock_id: str, trades: List[TradeRecord], date: str) -> Dict:
        """
        計算交易統計數據

        Args:
            stock_id: 股票代碼
            trades: 交易記錄列表
            date: 交易日期

        Returns:
            統計數據字典
        """
        # 取得公司名稱
        company_name = self.data_processor.get_company_name(stock_id) if self.data_processor else stock_id

        # 基本統計
        total_trades = len(trades)
        win_trades = 0
        loss_trades = 0
        total_entry_price = 0.0
        total_exit_price = 0.0
        total_pnl = 0.0

        for trade in trades:
            # 記錄進場價格
            entry_price = trade.entry_price
            total_entry_price += entry_price

            # 計算出場價格
            exit_price = self._calculate_exit_price(trade)
            total_exit_price += exit_price

            # 計算單筆損益
            single_pnl = (exit_price - entry_price) * 1000  # 每筆交易1張
            total_pnl += single_pnl

            # 判斷勝負
            if exit_price > trade.entry_price:
                win_trades += 1
            elif exit_price < trade.entry_price:
                loss_trades += 1

        # 計算平均價格和勝率
        avg_entry_price = total_entry_price / total_trades if total_trades > 0 else 0
        avg_exit_price = total_exit_price / total_trades if total_trades > 0 else 0
        win_rate = (win_trades / total_trades * 100) if total_trades > 0 else 0

        return {
            'stock_id': stock_id,
            '股票名稱': company_name,
            '進場次數': total_trades,
            '停損次數': loss_trades,
            '勝率(%)': round(win_rate, 2),
            '進場價格': round(avg_entry_price, 2),
            '出場價格': round(avg_exit_price, 2),
            '損益': round(total_pnl, 0)
        }

    def _calculate_exit_price(self, trade: TradeRecord) -> float:
        """
        計算實際出場價格

        Args:
            trade: 交易記錄

        Returns:
            出場價格
        """
        exit_price = 0.0

        if trade.trailing_exit_details:
            # 移動停利出場：計算加權平均價
            total_ratio = 0.0
            weighted_price = 0.0
            for exit in trade.trailing_exit_details:
                weighted_price += exit['price'] * exit['ratio']
                total_ratio += exit['ratio']

            # 加上最終出場（如進場價保護）
            if trade.final_exit_price and total_ratio < 1.0:
                remaining_ratio = 1.0 - total_ratio
                weighted_price += trade.final_exit_price * remaining_ratio
                total_ratio = 1.0

            exit_price = weighted_price / total_ratio if total_ratio > 0 else trade.entry_price
        elif trade.partial_exit_price and trade.final_exit_price:
            # 兩階段出場（原邏輯）
            exit_price = (trade.partial_exit_price * 0.5 + trade.final_exit_price * 0.5)
        elif trade.partial_exit_price:
            exit_price = trade.partial_exit_price
        elif trade.final_exit_price:
            exit_price = trade.final_exit_price
        else:
            exit_price = trade.entry_price

        return exit_price