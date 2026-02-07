"""
核心回測引擎
協調各模組執行回測邏輯
"""
import os
import logging
import pandas as pd
from typing import Dict, List, Optional, Tuple
from datetime import datetime

# 匯入策略模組
from strategy_modules.config_loader import StrategyConfig
from strategy_modules.entry_logic import EntryChecker, EntrySignal
from strategy_modules.exit_logic import ExitManager
from strategy_modules.position_manager import Position, PositionManager, ReentryManager, TradeRecord
from strategy_modules.indicators import (
    DayHighMomentumTracker,
    OrderBookBalanceMonitor,
    OutsideVolumeTracker,
    MassiveMatchingTracker,
    InsideOutsideRatioTracker
)
from strategy_modules.data_processor import DataProcessor

# 匯入分析模組
from analytics.trade_statistics import TradeStatisticsCalculator
from analytics.report_generator import ReportGenerator
from analytics.trade_details import TradeDetailsProcessor

# 匯入輸出模組
from exporters.csv_exporter import CSVExporter
from exporters.trade_exporter import TradeExporter

# 匯入視覺化模組
from visualization.chart_creator import ChartCreator

# 匯入常數
from core.constants import *

logger = logging.getLogger(__name__)


class BacktestEngine:
    """精簡化的回測引擎"""

    def __init__(self, config_path: str = DEFAULT_CONFIG_PATH):
        """
        初始化回測引擎

        Args:
            config_path: 設定檔路徑
        """
        # 載入設定
        self.config = StrategyConfig(config_path)
        self.entry_config = self.config.get_entry_config()
        self.exit_config = self.config.get_exit_config()
        self.reentry_config = self.config.get_reentry_config()

        # 初始化核心模組
        self._init_core_modules()

        # 初始化分析輸出模組
        self._init_analytics_modules()

        # 初始化指標追蹤器
        self._init_indicators()

        # 進場訊號記錄
        self.entry_signal_log: List[Dict] = []

        # 保存最後的tick資料供圖表使用
        self.last_tick_data = None

    def _init_core_modules(self):
        """初始化核心模組"""
        self.data_processor = DataProcessor(self.config.get_all_config())
        self.entry_checker = EntryChecker(self.entry_config)
        self.exit_manager = ExitManager(self.exit_config)
        self.position_manager = PositionManager()
        self.reentry_manager = ReentryManager(self.reentry_config)
        self.chart_creator = ChartCreator(self.config.get_all_config())

    def _init_analytics_modules(self):
        """初始化分析和輸出模組"""
        self.statistics_calculator = TradeStatisticsCalculator(self.data_processor)
        self.report_generator = ReportGenerator(self.entry_checker)
        self.details_processor = TradeDetailsProcessor()
        # 從設定檔讀取輸出路徑
        output_path = self.config.get_all_config().get('output_path', OUTPUT_BASE_DIR)
        self.csv_exporter = CSVExporter(output_path)
        self.trade_exporter = TradeExporter(output_path)

    def _init_indicators(self):
        """初始化技術指標追蹤器"""
        self.momentum_tracker = DayHighMomentumTracker(window_seconds=DAY_HIGH_MOMENTUM_WINDOW)
        self.orderbook_monitor = OrderBookBalanceMonitor(
            thin_threshold=ORDER_BOOK_THIN_THRESHOLD,
            normal_threshold=ORDER_BOOK_NORMAL_THRESHOLD
        )
        self.outside_volume_tracker = OutsideVolumeTracker(window_seconds=OUTSIDE_VOLUME_WINDOW)
        self.massive_matching_tracker = MassiveMatchingTracker(window_seconds=MASSIVE_MATCHING_WINDOW)
        self.inside_outside_ratio_tracker = InsideOutsideRatioTracker(window_seconds=IO_RATIO_WINDOW)
        self.large_order_io_ratio_tracker = InsideOutsideRatioTracker(
            window_seconds=IO_RATIO_WINDOW,
            min_volume_threshold=LARGE_ORDER_THRESHOLD
        )

    def run_single_backtest(self, stock_id: str, date: str, silent: bool = False) -> List[TradeRecord]:
        """
        執行單一股票回測

        Args:
            stock_id: 股票代碼
            date: 日期 (YYYY-MM-DD)
            silent: 是否為靜默模式（不輸出詳細日誌）

        Returns:
            交易記錄列表
        """
        # 重設持倉管理器狀態
        self.position_manager.reset()

        # 設定日誌等級
        original_level = logger.level
        if silent:
            logger.setLevel(logging.WARNING)

        logger.info(f"\n{'='*60}")
        logger.info(f"開始回測: {stock_id} - {date}")
        logger.info(f"{'='*60}")

        # 1. 載入並處理資料
        df = self._load_and_process_data(stock_id, date)
        if df.empty:
            return []

        # 2. 取得價格資訊
        ref_price, limit_up_price, limit_down_price = self._get_price_info(df, stock_id, date)

        # 3. 執行回測
        from core.backtest_loop import BacktestLoop
        backtest_loop = BacktestLoop(self)
        trades = backtest_loop.run(df, stock_id, ref_price, limit_up_price)

        # 4. 產生報告和視覺化（非靜默模式）
        if not silent:
            self._generate_outputs(df, trades, stock_id, date, ref_price, limit_up_price)

        # 保存tick資料供圖表使用
        self.last_tick_data = df

        # 恢復原始日誌等級
        if silent:
            logger.setLevel(original_level)

        return trades

    def _load_and_process_data(self, stock_id: str, date: str) -> pd.DataFrame:
        """載入並處理資料"""
        # 載入資料
        df = self.data_processor.load_feature_data(stock_id, date)
        if df.empty:
            logger.error(f"無法載入資料: {stock_id} - {date}")
            return pd.DataFrame()

        # 處理資料
        df = self.data_processor.process_trade_data(df)
        if df.empty:
            logger.error(f"處理資料後為空: {stock_id} - {date}")
            return pd.DataFrame()

        return df

    def _get_price_info(self, df: pd.DataFrame, stock_id: str, date: str) -> Tuple[float, float, float]:
        """取得參考價格和漲跌停價"""
        # 取得參考價格
        ref_price = self.data_processor.get_reference_price(stock_id, date)
        if ref_price is None:
            ref_price = df.iloc[0]['price'] if len(df) > 0 else 0
            logger.warning(f"使用開盤價作為參考價: {ref_price:.2f}")

        # 計算漲跌停價
        limit_up_price = self.data_processor.calculate_limit_up(ref_price)
        limit_down_price = self.data_processor.calculate_limit_down(ref_price)

        logger.info(f"參考價: {ref_price:.2f}, 漲停價: {limit_up_price:.2f}, 跌停價: {limit_down_price:.2f}")

        return ref_price, limit_up_price, limit_down_price

    def _generate_outputs(self, df: pd.DataFrame, trades: List[TradeRecord],
                          stock_id: str, date: str, ref_price: float, limit_up_price: float):
        """產生報告和視覺化輸出"""
        # 產生文字報告
        self.report_generator.generate_report(df, trades, stock_id, date)

        # 輸出詳細交易記錄到CSV
        if trades:
            self.trade_exporter.export_trade_details_to_csv(trades, stock_id, date)

        # 產生視覺化圖表
        self._create_visualization(df, trades, stock_id, date, ref_price, limit_up_price)

    def _create_visualization(self, df: pd.DataFrame, trades: List[TradeRecord],
                             stock_id: str, date: str, ref_price: float, limit_up_price: float):
        """產生視覺化圖表"""
        try:
            # 將 TradeRecord 物件轉換成字典格式
            trades_dict = []
            for trade in trades:
                trade_dict = {
                    'entry_time': trade.entry_time,
                    'entry_price': trade.entry_price,
                    'entry_ratio': getattr(trade, 'entry_ratio', 0),
                    'partial_exit_time': trade.partial_exit_time,
                    'partial_exit_price': trade.partial_exit_price,
                    'partial_exit_reason': getattr(trade, 'partial_exit_reason', ''),
                    'reentry_time': getattr(trade, 'reentry_time', None),
                    'reentry_price': getattr(trade, 'reentry_price', None),
                    'final_exit_time': trade.final_exit_time,
                    'final_exit_price': trade.final_exit_price,
                    'final_exit_reason': getattr(trade, 'final_exit_reason', ''),
                    'trailing_exit_details': getattr(trade, 'trailing_exit_details', []),
                    'pnl_percent': trade.pnl_percent
                }
                trades_dict.append(trade_dict)

            # 建立圖表
            # 從設定檔讀取輸出路徑，如果沒有則使用預設值
            config_dict = self.config.get_all_config()
            output_base_dir = config_dict.get('output_path', r"D:\回測結果")
            output_dir = os.path.join(output_base_dir, date)
            os.makedirs(output_dir, exist_ok=True)

            output_path = os.path.join(output_dir, f"{stock_id}_{date}_strategy.html")
            png_output_path = os.path.join(output_dir, f"{stock_id}_{date}_strategy.png")

            # 調用圖表創建方法
            output_path = self.chart_creator.create_strategy_chart(
                df=df,
                trades=trades_dict,  # 使用轉換後的字典列表
                output_path=output_path,
                png_output_path=png_output_path,
                ref_price=ref_price,
                limit_up_price=limit_up_price,
                stock_id=stock_id,
                company_name=None
            )

            if output_path:
                logger.info(f"圖表已儲存至: {output_path}")
        except Exception as e:
            logger.error(f"建立圖表時發生錯誤: {e}")

    def run_batch_backtest(self, stock_list: List[str], date: str,
                          output_csv: bool = True, create_charts: bool = True) -> List[Dict]:
        """
        執行批次回測

        Args:
            stock_list: 股票代碼列表
            date: 回測日期
            output_csv: 是否輸出CSV
            create_charts: 是否產生圖表

        Returns:
            回測結果列表
        """
        results = []
        all_trade_details = []

        for stock_id in stock_list:
            logger.info(f"\n處理股票: {stock_id}")

            # 執行單一股票回測
            trades = self.run_single_backtest(stock_id, date, silent=not create_charts)

            # 計算統計
            if trades:
                stats = self.statistics_calculator.calculate_statistics(stock_id, trades, date)
                results.append(stats)

                # 收集詳細交易記錄
                trade_details = self.details_processor.collect_trade_details(trades, stock_id, date)
                all_trade_details.extend(trade_details)

        # 輸出結果
        if output_csv and results:
            self.csv_exporter.export_detailed_trades_to_csv(all_trade_details, date)

        # 產生總結報告
        if results:
            summary_df = self.report_generator.generate_summary_report(results, date)

        return results