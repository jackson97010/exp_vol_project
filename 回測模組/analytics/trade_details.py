"""
交易詳情處理模組
負責收集和處理詳細的交易記錄
"""
from typing import Dict, List
from strategy_modules.position_manager import TradeRecord


class TradeDetailsProcessor:
    """交易詳情處理器"""

    def collect_trade_details(self, trades: List[TradeRecord], stock_id: str, date: str) -> List[Dict]:
        """
        收集單一股票的詳細交易記錄

        Args:
            trades: 交易記錄列表
            stock_id: 股票代碼
            date: 日期

        Returns:
            詳細交易記錄列表
        """
        trade_details = []

        for i, trade in enumerate(trades, 1):
            # 計算實際出場價格（加權平均）
            exit_price = self.calculate_actual_exit_price(trade)
            pnl = (exit_price - trade.entry_price) * 1000  # 1張股票
            pnl_percent = ((exit_price - trade.entry_price) / trade.entry_price) * 100

            # 處理移動停利出場（可能有多次）
            if trade.trailing_exit_details:
                self._add_trailing_exit_details(trade_details, trade, i, stock_id, exit_price, pnl, pnl_percent)

            # 處理兩階段出場模式
            elif trade.partial_exit_time or trade.final_exit_time:
                self._add_two_stage_exit_details(trade_details, trade, i, stock_id, exit_price, pnl, pnl_percent)

        return trade_details

    def calculate_actual_exit_price(self, trade: TradeRecord) -> float:
        """
        計算實際出場價格（加權平均）

        Args:
            trade: 交易記錄

        Returns:
            加權平均出場價格
        """
        if trade.trailing_exit_details:
            # 移動停利出場：計算加權平均價
            total_ratio = 0.0
            weighted_price = 0.0

            for exit_detail in trade.trailing_exit_details:
                weighted_price += exit_detail['price'] * exit_detail['ratio']
                total_ratio += exit_detail['ratio']

            # 如果還有剩餘部位（進場價保護或收盤）
            if trade.final_exit_price and total_ratio < 1.0:
                remaining_ratio = 1.0 - total_ratio
                weighted_price += trade.final_exit_price * remaining_ratio
                total_ratio = 1.0

            return weighted_price / total_ratio if total_ratio > 0 else trade.entry_price

        elif trade.partial_exit_price and trade.final_exit_price:
            # 兩階段出場
            if trade.reentry_price:
                # 有回補：50%在partial_exit，50%在final_exit
                return (trade.partial_exit_price * 0.5 + trade.final_exit_price * 0.5)
            else:
                # 無回補：50%在partial_exit，50%在final_exit
                return (trade.partial_exit_price * 0.5 + trade.final_exit_price * 0.5)

        elif trade.partial_exit_price:
            # 只有部分出場
            return trade.partial_exit_price

        elif trade.final_exit_price:
            # 只有最終出場
            return trade.final_exit_price

        else:
            # 沒有出場（理論上不應該發生）
            return trade.entry_price

    def _add_trailing_exit_details(self, trade_details: List[Dict], trade: TradeRecord, trade_num: int,
                                    stock_id: str, exit_price: float, pnl: float, pnl_percent: float) -> None:
        """添加移動停利出場詳情"""
        # 每次移動停利出場
        for j, exit_detail in enumerate(trade.trailing_exit_details):
            detail = {
                '股票代碼': stock_id,
                '交易編號': trade_num,
                '進場時間': trade.entry_time,
                '進場價格': trade.entry_price,
                '進場Ratio': trade.entry_ratio,
                '進場外盤3秒(M)': trade.entry_outside_volume_3s / 1000000 if trade.entry_outside_volume_3s else 0,
                '出場批次': j + 1,
                '出場類型': f"移動停利_{exit_detail['level']}",
                '出場時間': exit_detail['time'],
                '出場價格': exit_detail['price'],
                '出場比例': exit_detail['ratio'] * 100,
                '出場原因': f"跌破{exit_detail['level']}低點",
                '實際出場均價': exit_price,
                '總損益金額': pnl,
                '總損益百分比': pnl_percent
            }
            trade_details.append(detail)

        # 如果有進場價保護或收盤出場（剩餘部位）
        if trade.final_exit_time and trade.final_exit_reason:
            remaining_ratio = 1.0 - sum(exit['ratio'] for exit in trade.trailing_exit_details)
            if remaining_ratio > 0:
                detail = {
                    '股票代碼': stock_id,
                    '交易編號': trade_num,
                    '進場時間': trade.entry_time,
                    '進場價格': trade.entry_price,
                    '進場Ratio': trade.entry_ratio,
                    '進場外盤3秒(M)': trade.entry_outside_volume_3s / 1000000 if trade.entry_outside_volume_3s else 0,
                    '出場批次': len(trade.trailing_exit_details) + 1,
                    '出場類型': '最終清倉',
                    '出場時間': trade.final_exit_time,
                    '出場價格': trade.final_exit_price,
                    '出場比例': remaining_ratio * 100,
                    '出場原因': trade.final_exit_reason,
                    '實際出場均價': exit_price,
                    '總損益金額': pnl,
                    '總損益百分比': pnl_percent
                }
                trade_details.append(detail)

    def _add_two_stage_exit_details(self, trade_details: List[Dict], trade: TradeRecord, trade_num: int,
                                     stock_id: str, exit_price: float, pnl: float, pnl_percent: float) -> None:
        """添加兩階段出場詳情"""
        batch = 1

        # 第一階段出場（減碼50%）
        if trade.partial_exit_time:
            detail = {
                '股票代碼': stock_id,
                '交易編號': trade_num,
                '進場時間': trade.entry_time,
                '進場價格': trade.entry_price,
                '進場Ratio': trade.entry_ratio,
                '進場外盤3秒(M)': trade.entry_outside_volume_3s / 1000000 if trade.entry_outside_volume_3s else 0,
                '出場批次': batch,
                '出場類型': '減碼50%',
                '出場時間': trade.partial_exit_time,
                '出場價格': trade.partial_exit_price,
                '出場比例': 50.0,
                '出場原因': trade.partial_exit_reason,
                '實際出場均價': exit_price,
                '總損益金額': pnl,
                '總損益百分比': pnl_percent
            }
            trade_details.append(detail)
            batch += 1

        # 回補進場（如果有）
        if trade.reentry_time:
            detail = {
                '股票代碼': stock_id,
                '交易編號': trade_num,
                '進場時間': trade.entry_time,
                '進場價格': trade.entry_price,
                '進場Ratio': trade.entry_ratio,
                '進場外盤3秒(M)': trade.entry_outside_volume_3s / 1000000 if trade.entry_outside_volume_3s else 0,
                '出場批次': '回補',
                '出場類型': '回補進場',
                '出場時間': trade.reentry_time,
                '出場價格': trade.reentry_price,
                '出場比例': '-',
                '出場原因': '價格創新高且外盤增加',
                '實際出場均價': exit_price,
                '總損益金額': pnl,
                '總損益百分比': pnl_percent
            }
            trade_details.append(detail)

        # 最終出場（清倉）
        if trade.final_exit_time:
            detail = {
                '股票代碼': stock_id,
                '交易編號': trade_num,
                '進場時間': trade.entry_time,
                '進場價格': trade.entry_price,
                '進場Ratio': trade.entry_ratio,
                '進場外盤3秒(M)': trade.entry_outside_volume_3s / 1000000 if trade.entry_outside_volume_3s else 0,
                '出場批次': batch,
                '出場類型': '清倉' if not trade.reentry_time else '回補後清倉',
                '出場時間': trade.final_exit_time,
                '出場價格': trade.final_exit_price,
                '出場比例': 50.0 if trade.partial_exit_time else 100.0,
                '出場原因': trade.final_exit_reason,
                '實際出場均價': exit_price,
                '總損益金額': pnl,
                '總損益百分比': pnl_percent
            }
            trade_details.append(detail)