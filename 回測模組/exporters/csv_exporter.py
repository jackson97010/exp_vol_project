"""
CSV 輸出模組
負責將回測結果輸出為 CSV 檔案
"""
import os
import logging
import pandas as pd
from typing import Dict, List
from strategy_modules.position_manager import TradeRecord

logger = logging.getLogger(__name__)

# 輸出路徑設定
OUTPUT_BASE_DIR = r"D:\回測結果"


class CSVExporter:
    """CSV 輸出器"""

    def __init__(self, output_base_dir: str = OUTPUT_BASE_DIR):
        """
        初始化 CSV 輸出器

        Args:
            output_base_dir: 輸出基礎目錄
        """
        self.output_base_dir = output_base_dir

    def export_summary_to_csv(self, results: List[Dict], date: str) -> None:
        """
        輸出統計結果到CSV

        Args:
            results: 統計結果列表
            date: 日期
        """
        if not results:
            logger.warning("沒有交易結果可輸出")
            return

        # 建立輸出目錄
        output_dir = os.path.join(self.output_base_dir, date)
        os.makedirs(output_dir, exist_ok=True)

        # 輸出檔案路徑
        csv_path = os.path.join(output_dir, f'backtest_summary_{date}.csv')

        # 寫入CSV
        df = pd.DataFrame(results)
        df.to_csv(csv_path, index=False, encoding='utf-8-sig')

        logger.info(f"\n統計結果已輸出至: {csv_path}")

        # 顯示總計
        total_pnl = df['損益'].sum()
        total_trades = df['進場次數'].sum()
        total_wins = sum(1 for _, row in df.iterrows() if row['損益'] > 0)

        logger.info("\n" + "=" * 60)
        logger.info(f"總計:")
        logger.info(f"  交易次數: {total_trades}")
        logger.info(f"  獲利次數: {total_wins}")
        if total_trades > 0:
            logger.info(f"  總勝率: {total_wins/total_trades*100:.2f}%")
        logger.info(f"  總損益: {total_pnl:,.0f}")
        logger.info("=" * 60)

    def export_detailed_trades_to_csv(self, all_trade_details: List[Dict], date: str) -> None:
        """
        輸出所有股票的詳細交易記錄到總表

        Args:
            all_trade_details: 所有股票的詳細交易記錄
            date: 日期
        """
        if not all_trade_details:
            logger.warning("沒有交易記錄可輸出")
            return

        # 建立輸出目錄
        output_dir = os.path.join(self.output_base_dir, date)
        os.makedirs(output_dir, exist_ok=True)

        # 輸出檔案路徑（使用原本的總表檔名）
        csv_path = os.path.join(output_dir, f'backtest_summary_{date}.csv')

        # 轉換為DataFrame並輸出
        df = pd.DataFrame(all_trade_details)

        # 依股票代碼和進場時間排序
        df = df.sort_values(['股票代碼', '進場時間', '出場批次'])

        # 輸出CSV
        df.to_csv(csv_path, index=False, encoding='utf-8-sig')

        logger.info(f"\n詳細交易記錄已輸出至: {csv_path}")

        # 顯示總計統計
        total_trades = df['交易編號'].groupby(df['股票代碼']).max().sum()
        total_pnl = df.groupby(['股票代碼', '交易編號'])['總損益金額'].first().sum()
        winning_trades = df.groupby(['股票代碼', '交易編號'])['總損益金額'].first()
        total_wins = sum(1 for pnl in winning_trades if pnl > 0)

        logger.info("\n" + "=" * 60)
        logger.info(f"總計:")
        logger.info(f"  股票數量: {df['股票代碼'].nunique()}")
        logger.info(f"  交易次數: {total_trades}")
        logger.info(f"  獲利次數: {total_wins}")
        if total_trades > 0:
            logger.info(f"  總勝率: {total_wins/total_trades*100:.2f}%")
        logger.info(f"  總損益: {total_pnl:,.0f}")
        logger.info("=" * 60)

        # 同時產生批次腳本需要的 backtest_results_{date}.csv 檔案（在專案根目錄）
        self._export_batch_script_csv(all_trade_details, date)

    def _export_batch_script_csv(self, all_trade_details: List[Dict], date: str) -> None:
        """
        產生批次腳本需要的 backtest_results_{date}.csv 檔案
        格式: trade_id,entry_time,stock_id,entry_price,exit_price,exit_ratio,exit_type

        重要：trade_id 不包含出場批次編號，以便 trade_pnl_analyzer.py 能正確
        將同一筆交易的多個出場批次分組，計算加權平均出場價格

        Args:
            all_trade_details: 所有股票的詳細交易記錄
            date: 日期
        """
        if not all_trade_details:
            return

        # 轉換為批次腳本需要的格式
        batch_records = []
        for detail in all_trade_details:
            record = {
                'trade_id': f"{detail['股票代碼']}_T{detail['交易編號']}",  # 不包含 _B{出場批次}
                'entry_time': detail['進場時間'],
                'stock_id': detail['股票代碼'],
                'entry_price': detail['進場價格'],
                'exit_price': detail['出場價格'],
                'exit_ratio': detail['出場比例'],
                'exit_type': detail['出場原因']
            }
            batch_records.append(record)

        # 輸出到專案根目錄
        batch_csv_path = f'backtest_results_{date}.csv'
        df = pd.DataFrame(batch_records)
        df.to_csv(batch_csv_path, index=False, encoding='utf-8-sig')

        logger.info(f"批次腳本CSV已輸出至: {batch_csv_path}")