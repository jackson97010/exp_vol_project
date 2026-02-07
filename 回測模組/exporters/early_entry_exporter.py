"""
早盤進場策略專用輸出模組
負責將進出場點位記錄到 D:/1分進場 資料夾
並產生進出場點位圖
"""

import os
import json
import pandas as pd
from datetime import datetime
from typing import List, Dict, Optional
import logging
from .early_chart_creator import EarlyChartCreator

logger = logging.getLogger(__name__)


class EarlyEntryExporter:
    """早盤進場策略輸出器"""

    def __init__(self, base_path: str = "D:/1分進場", config: Optional[Dict] = None):
        """
        初始化輸出器

        Args:
            base_path: 基礎輸出路徑
            config: 策略配置（用於圖表產生）
        """
        self.base_path = base_path
        self.current_date = None
        self.trades_buffer = []  # 暫存所有交易記錄
        self.config = config or {}
        self.chart_creator = EarlyChartCreator(config)

    def set_date(self, date: str):
        """
        設定當前日期並建立相應資料夾

        Args:
            date: 日期字串 (YYYY-MM-DD)
        """
        self.current_date = date
        self.date_path = os.path.join(self.base_path, date)

        # 建立日期資料夾
        os.makedirs(self.date_path, exist_ok=True)
        logger.info(f"設定輸出路徑: {self.date_path}")

    def add_trade(self, trade_record: Dict):
        """
        新增交易記錄到緩衝區

        Args:
            trade_record: 交易記錄字典
        """
        self.trades_buffer.append(trade_record)

    def export_single_trade(
        self,
        stock_id: str,
        trade_record: Dict,
        df: Optional[pd.DataFrame] = None,
        massive_threshold: Optional[float] = None
    ):
        """
        輸出單一股票的交易記錄（含圖表）

        Args:
            stock_id: 股票代碼
            trade_record: 交易記錄
            df: Tick資料 DataFrame（用於圖表產生）
            massive_threshold: 大量搓合門檻（元）
        """
        if not self.current_date:
            logger.error("未設定日期，無法輸出")
            return

        # 建立股票資料夾
        stock_path = os.path.join(self.date_path, stock_id)
        os.makedirs(stock_path, exist_ok=True)

        # 準備輸出資料
        entry_data = {
            '日期': self.current_date,
            '股票代碼': stock_id,
            '進場時間': trade_record.get('entry_time', ''),
            '進場價格': trade_record.get('entry_price', 0),
            '進場Ratio': trade_record.get('entry_ratio', 0)
        }

        # 處理出場資料（可能有多筆）
        exit_data = []
        if 'exits' in trade_record:
            for i, exit_record in enumerate(trade_record['exits'], 1):
                exit_data.append({
                    f'出場{i}_時間': exit_record.get('time', ''),
                    f'出場{i}_價格': exit_record.get('price', 0),
                    f'出場{i}_原因': exit_record.get('reason', ''),
                    f'出場{i}_比例': exit_record.get('ratio', 0)
                })

        # 計算損益
        profit_data = self._calculate_profit(trade_record)

        # 合併所有資料
        complete_data = {**entry_data}
        for exit_dict in exit_data:
            complete_data.update(exit_dict)
        complete_data.update(profit_data)

        # 輸出 CSV
        csv_path = os.path.join(stock_path, 'entry_exit.csv')
        df_csv = pd.DataFrame([complete_data])
        df_csv.to_csv(csv_path, index=False, encoding='utf-8-sig')
        logger.info(f"已輸出交易記錄: {csv_path}")

        # 輸出 JSON（詳細資料）
        json_path = os.path.join(stock_path, 'trade_detail.json')
        with open(json_path, 'w', encoding='utf-8') as f:
            json.dump(trade_record, f, ensure_ascii=False, indent=2, default=str)

        # 產生圖表（如果有 df 資料）
        if df is not None and len(df) > 0:
            try:
                chart_path = os.path.join(stock_path, 'chart.html')
                threshold = massive_threshold if massive_threshold is not None else 1000000.0

                self.chart_creator.create_chart(
                    df=df,
                    trade_record=trade_record,
                    output_path=chart_path,
                    stock_id=stock_id,
                    date=self.current_date,
                    massive_threshold=threshold
                )
                logger.info(f"已產生圖表: {chart_path}")
            except Exception as e:
                logger.error(f"圖表產生失敗: {e}", exc_info=True)

    def _calculate_profit(self, trade_record: Dict) -> Dict:
        """
        計算交易損益（使用加權平均出場價格）

        Args:
            trade_record: 交易記錄

        Returns:
            損益資料字典
        """
        entry_price = trade_record.get('entry_price', 0)
        stock_id = trade_record.get('stock_id', 'Unknown')
        total_shares = 12 * 1000  # 12張 * 1000股

        # 計算加權平均出場價格
        if 'exits' in trade_record and trade_record['exits']:
            weighted_exit_sum = 0
            total_exit_ratio = 0

            for exit_record in trade_record['exits']:
                ratio = exit_record.get('ratio', 0)
                price = exit_record.get('price', 0)
                weighted_exit_sum += (price * ratio)
                total_exit_ratio += ratio

            # 檢查總比例是否異常（容許 0.1% 的浮點誤差）
            if total_exit_ratio > 1.001:
                logger.warning(
                    f"警告：{stock_id} 出場比例總計 {total_exit_ratio:.4f} 超過 100%，"
                    f"可能存在重複計算！"
                )
            elif total_exit_ratio < 0.999:
                logger.warning(
                    f"警告：{stock_id} 出場比例總計 {total_exit_ratio:.4f} 不足 100%，"
                    f"可能有未記錄的出場批次！"
                )

            # 計算加權平均出場價格
            if total_exit_ratio > 0:
                avg_exit_price = weighted_exit_sum / total_exit_ratio
            else:
                # 如果沒有比例資訊，使用簡單平均
                exit_prices = [e.get('price', 0) for e in trade_record['exits']]
                avg_exit_price = sum(exit_prices) / len(exit_prices) if exit_prices else 0
                logger.warning(f"警告：{stock_id} 沒有出場比例資訊，使用簡單平均")
        else:
            avg_exit_price = 0
            logger.warning(f"警告：{stock_id} 沒有出場記錄")

        # 計算損益（根據 Backtest_Specification.md 規範）
        position_value = entry_price * total_shares
        exit_value = avg_exit_price * total_shares

        # 手續費計算：進場價值 * 0.0017
        commission = position_value * 0.0017

        # 淨損益 = 出場價值 - 進場價值 - 手續費
        profit = exit_value - position_value - commission

        # 報酬率 = 淨損益 / 進場價值 * 100%
        return_rate = (profit / position_value) * 100 if position_value > 0 else 0

        return {
            '平均出場價格': round(avg_exit_price, 2),
            '損益金額': round(profit, 2),
            '報酬率%': round(return_rate, 2),
            '手續費': round(commission, 2)
        }

    def export_summary(self):
        """
        輸出當日所有交易摘要
        """
        if not self.current_date or not self.trades_buffer:
            logger.info("無交易資料需要輸出")
            return

        # 準備摘要資料
        summary_data = []
        for trade in self.trades_buffer:
            profit_data = self._calculate_profit(trade)

            summary_data.append({
                '股票代碼': trade.get('stock_id', ''),
                '進場時間': trade.get('entry_time', ''),
                '進場價格': trade.get('entry_price', 0),
                '平均出場價格': profit_data['平均出場價格'],
                '損益金額': profit_data['損益金額'],
                '報酬率%': profit_data['報酬率%']
            })

        # 輸出摘要 CSV
        summary_path = os.path.join(self.date_path, 'summary.csv')
        df = pd.DataFrame(summary_data)

        # 加入統計資料
        if len(df) > 0:
            stats = pd.DataFrame([{
                '股票代碼': '總計',
                '進場時間': '',
                '進場價格': '',
                '平均出場價格': '',
                '損益金額': df['損益金額'].sum(),
                '報酬率%': df['報酬率%'].mean()
            }])
            df = pd.concat([df, stats], ignore_index=True)

        df.to_csv(summary_path, index=False, encoding='utf-8-sig')
        logger.info(f"已輸出交易摘要: {summary_path}")

        # 輸出詳細 JSON
        detail_path = os.path.join(self.date_path, 'trades_detail.json')
        with open(detail_path, 'w', encoding='utf-8') as f:
            json.dump(self.trades_buffer, f, ensure_ascii=False, indent=2, default=str)

        logger.info(f"共輸出 {len(self.trades_buffer)} 筆交易記錄")

    def export_performance_report(self):
        """
        輸出績效報表（可選）
        """
        if not self.trades_buffer:
            return

        # 計算各種績效指標
        total_trades = len(self.trades_buffer)
        profitable_trades = sum(1 for t in self.trades_buffer
                              if self._calculate_profit(t)['損益金額'] > 0)

        win_rate = (profitable_trades / total_trades) * 100 if total_trades > 0 else 0

        all_profits = [self._calculate_profit(t)['損益金額'] for t in self.trades_buffer]
        total_profit = sum(all_profits)
        avg_profit = sum(all_profits) / len(all_profits) if all_profits else 0

        all_returns = [self._calculate_profit(t)['報酬率%'] for t in self.trades_buffer]
        avg_return = sum(all_returns) / len(all_returns) if all_returns else 0

        performance = {
            '交易日期': self.current_date,
            '總交易次數': total_trades,
            '獲利次數': profitable_trades,
            '勝率%': round(win_rate, 2),
            '總損益': round(total_profit, 2),
            '平均損益': round(avg_profit, 2),
            '平均報酬率%': round(avg_return, 2),
            '策略模式': '早盤突破(09:01-09:05)'
        }

        # 輸出績效報表
        report_path = os.path.join(self.base_path, 'reports')
        os.makedirs(report_path, exist_ok=True)

        report_file = os.path.join(report_path, f'performance_{self.current_date}.json')
        with open(report_file, 'w', encoding='utf-8') as f:
            json.dump(performance, f, ensure_ascii=False, indent=2)

        logger.info(f"已輸出績效報表: {report_file}")
        logger.info(f"勝率: {win_rate:.2f}%, 平均報酬率: {avg_return:.2f}%")