"""
交易記錄輸出模組
負責輸出個股的詳細交易記錄
"""
import os
import logging
import pandas as pd
from typing import List
from strategy_modules.position_manager import TradeRecord
from analytics.trade_details import TradeDetailsProcessor

logger = logging.getLogger(__name__)

# 輸出路徑設定
OUTPUT_BASE_DIR = r"D:\回測結果"


class TradeExporter:
    """交易記錄輸出器"""

    def __init__(self, output_base_dir: str = OUTPUT_BASE_DIR):
        """
        初始化交易記錄輸出器

        Args:
            output_base_dir: 輸出基礎目錄
        """
        self.output_base_dir = output_base_dir
        self.details_processor = TradeDetailsProcessor()

    def export_trade_details_to_csv(self, trades: List[TradeRecord], stock_id: str, date: str) -> None:
        """
        輸出詳細的進出場記錄到CSV（每次出場都有獨立的一行）

        Args:
            trades: 交易記錄列表
            stock_id: 股票代碼
            date: 日期
        """
        if not trades:
            logger.warning(f"股票 {stock_id} 沒有交易記錄可輸出")
            return

        # 建立輸出目錄
        output_dir = os.path.join(self.output_base_dir, date)
        os.makedirs(output_dir, exist_ok=True)

        # 輸出檔案路徑
        csv_path = os.path.join(output_dir, f'{stock_id}_trade_details_{date}.csv')

        # 準備交易詳細資料（每次出場都有獨立的一行）
        trade_details = []

        for i, trade in enumerate(trades, 1):
            # 計算實際出場價格（加權平均）
            exit_price = self.details_processor.calculate_actual_exit_price(trade)
            pnl = (exit_price - trade.entry_price) * 1000  # 1張股票
            pnl_percent = ((exit_price - trade.entry_price) / trade.entry_price) * 100

            # 處理移動停利出場（可能有多次）
            if trade.trailing_exit_details:
                self._add_trailing_exit_rows(trade_details, trade, i, stock_id, exit_price, pnl, pnl_percent)

            # 處理兩階段出場模式
            elif trade.partial_exit_time or trade.final_exit_time:
                self._add_two_stage_exit_rows(trade_details, trade, i, stock_id, exit_price, pnl, pnl_percent)

            # 如果沒有任何出場記錄（理論上不應該發生）
            else:
                self._add_no_exit_row(trade_details, trade, i, stock_id)

        # 轉換為DataFrame並輸出
        if trade_details:
            df = pd.DataFrame(trade_details)
            df.to_csv(csv_path, index=False, encoding='utf-8-sig')
            logger.info(f"詳細交易記錄已輸出至: {csv_path}")
            logger.info(f"總共輸出 {len(trade_details)} 筆出場記錄")

    def _add_trailing_exit_rows(self, trade_details: list, trade: TradeRecord, trade_num: int,
                                 stock_id: str, exit_price: float, pnl: float, pnl_percent: float) -> None:
        """添加移動停利出場記錄"""
        for j, exit_detail in enumerate(trade.trailing_exit_details):
            detail = {
                '交易編號': trade_num,
                '股票代碼': stock_id,
                '進場時間': trade.entry_time,
                '進場價格': trade.entry_price,
                '進場Ratio': trade.entry_ratio,
                '進場外盤3秒(M)': trade.entry_outside_volume_3s / 1000000 if trade.entry_outside_volume_3s else 0,
                '進場時DayHigh': trade.day_high_at_entry,
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
                    '交易編號': trade_num,
                    '股票代碼': stock_id,
                    '進場時間': trade.entry_time,
                    '進場價格': trade.entry_price,
                    '進場Ratio': trade.entry_ratio,
                    '進場外盤3秒(M)': trade.entry_outside_volume_3s / 1000000 if trade.entry_outside_volume_3s else 0,
                    '進場時DayHigh': trade.day_high_at_entry,
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

    def _add_two_stage_exit_rows(self, trade_details: list, trade: TradeRecord, trade_num: int,
                                  stock_id: str, exit_price: float, pnl: float, pnl_percent: float) -> None:
        """添加兩階段出場記錄"""
        batch = 1

        # 第一階段出場（減碼50%）
        if trade.partial_exit_time:
            detail = {
                '交易編號': trade_num,
                '股票代碼': stock_id,
                '進場時間': trade.entry_time,
                '進場價格': trade.entry_price,
                '進場Ratio': trade.entry_ratio,
                '進場外盤3秒(M)': trade.entry_outside_volume_3s / 1000000 if trade.entry_outside_volume_3s else 0,
                '進場時DayHigh': trade.day_high_at_entry,
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
                '交易編號': trade_num,
                '股票代碼': stock_id,
                '進場時間': trade.entry_time,
                '進場價格': trade.entry_price,
                '進場Ratio': trade.entry_ratio,
                '進場外盤3秒(M)': trade.entry_outside_volume_3s / 1000000 if trade.entry_outside_volume_3s else 0,
                '進場時DayHigh': trade.day_high_at_entry,
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
                '交易編號': trade_num,
                '股票代碼': stock_id,
                '進場時間': trade.entry_time,
                '進場價格': trade.entry_price,
                '進場Ratio': trade.entry_ratio,
                '進場外盤3秒(M)': trade.entry_outside_volume_3s / 1000000 if trade.entry_outside_volume_3s else 0,
                '進場時DayHigh': trade.day_high_at_entry,
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

    def _add_no_exit_row(self, trade_details: list, trade: TradeRecord, trade_num: int, stock_id: str) -> None:
        """添加無出場記錄（理論上不應該發生）"""
        detail = {
            '交易編號': trade_num,
            '股票代碼': stock_id,
            '進場時間': trade.entry_time,
            '進場價格': trade.entry_price,
            '進場Ratio': trade.entry_ratio,
            '進場外盤3秒(M)': trade.entry_outside_volume_3s / 1000000 if trade.entry_outside_volume_3s else 0,
            '進場時DayHigh': trade.day_high_at_entry,
            '出場批次': '-',
            '出場類型': '未出場',
            '出場時間': '-',
            '出場價格': '-',
            '出場比例': '-',
            '出場原因': '-',
            '實際出場均價': trade.entry_price,
            '總損益金額': 0,
            '總損益百分比': 0
        }
        trade_details.append(detail)