"""
報告生成模組
負責產生回測報告和分析結果
"""
import logging
from typing import List
import pandas as pd
from strategy_modules.position_manager import TradeRecord

logger = logging.getLogger(__name__)


class ReportGenerator:
    """報告生成器"""

    def __init__(self, entry_checker=None):
        """
        初始化報告生成器

        Args:
            entry_checker: 進場檢查器，用於取得進場訊號資訊
        """
        self.entry_checker = entry_checker

    def generate_report(self, df: pd.DataFrame, trades: List[TradeRecord], stock_id: str, date: str) -> None:
        """
        產生回測報告

        Args:
            df: 交易資料 DataFrame
            trades: 交易記錄列表
            stock_id: 股票代碼
            date: 交易日期
        """
        logger.info(f"\n{'='*60}")
        logger.info(f"回測報告 - {stock_id} ({date})")
        logger.info(f"{'='*60}")

        # 交易統計
        logger.info(f"\n交易統計:")
        logger.info(f"  總交易次數: {len(trades)}")

        # 詳細交易記錄
        self._log_trade_details(trades)

        # 進場訊號統計
        self._log_entry_signals()

    def _log_trade_details(self, trades: List[TradeRecord]) -> None:
        """
        輸出詳細交易記錄

        Args:
            trades: 交易記錄列表
        """
        for i, trade in enumerate(trades, 1):
            logger.info(f"\n交易 #{i}:")
            logger.info(f"  進場: {trade.entry_time} @ {trade.entry_price:.2f} (Ratio: {trade.entry_ratio:.1f})")

            if trade.partial_exit_time:
                logger.info(f"  減碼: {trade.partial_exit_time} @ {trade.partial_exit_price:.2f}")

            if trade.reentry_time:
                logger.info(f"  回補: {trade.reentry_time} @ {trade.reentry_price:.2f}")

            # 處理移動停利出場
            if hasattr(trade, 'trailing_exit_details') and trade.trailing_exit_details:
                for exit in trade.trailing_exit_details:
                    logger.info(f"  移動停利: {exit['time']} @ {exit['price']:.2f} ({exit['ratio']*100:.0f}%)")

            if trade.final_exit_time:
                logger.info(f"  清倉: {trade.final_exit_time} @ {trade.final_exit_price:.2f}")

            if trade.pnl_percent:
                logger.info(f"  收益: {trade.pnl_percent:.2f}%")

    def _log_entry_signals(self) -> None:
        """輸出進場訊號統計"""
        if self.entry_checker and hasattr(self.entry_checker, 'entry_signals'):
            if self.entry_checker.entry_signals:
                signal_df = self.entry_checker.get_entry_signals_summary()
                if not signal_df.empty:
                    logger.info("\n進場訊號分析:")
                    logger.info(f"  總進場訊號數: {len(signal_df)}")

                    # 檢查 passed 欄位存在並統計
                    if 'passed' in signal_df.columns:
                        passed_count = signal_df['passed'].sum()
                        passed_percentage = (passed_count / len(signal_df) * 100) if len(signal_df) > 0 else 0
                        logger.info(f"  通過訊號數: {passed_count}/{len(signal_df)} ({passed_percentage:.1f}%)")

                    # 顯示可用欄位（debug用）
                    logger.debug(f"  可用欄位: {list(signal_df.columns)}")

                    # 如果有條件欄位，統計各條件
                    if 'conditions' in signal_df.columns and len(signal_df) > 0:
                        # 取第一筆的條件作為範例
                        first_conditions = signal_df.iloc[0]['conditions']
                        if isinstance(first_conditions, dict):
                            logger.info("\n各條件統計:")
                            for condition_name in first_conditions.keys():
                                logger.info(f"  {condition_name}")

    def generate_summary_report(self, results: List[dict], date: str) -> pd.DataFrame:
        """
        產生批次回測總結報告

        Args:
            results: 批次回測結果列表
            date: 回測日期

        Returns:
            總結報告 DataFrame
        """
        if not results:
            logger.warning("無回測結果可供生成報告")
            return pd.DataFrame()

        # 建立 DataFrame
        df_results = pd.DataFrame(results)

        # 計算總計
        total_trades = df_results['進場次數'].sum()
        total_pnl = df_results['損益'].sum()
        avg_win_rate = df_results[df_results['進場次數'] > 0]['勝率(%)'].mean()

        logger.info(f"\n{'='*60}")
        logger.info(f"批次回測總結 - {date}")
        logger.info(f"{'='*60}")
        logger.info(f"總股票數: {len(results)}")
        logger.info(f"總交易次數: {total_trades}")
        logger.info(f"總損益: {total_pnl:,.0f}")
        logger.info(f"平均勝率: {avg_win_rate:.2f}%")

        return df_results