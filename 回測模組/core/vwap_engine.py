"""VWAP 執行策略回測引擎模組 (Adaptive 版)

主協調器，串接資料載入、Volume Profile 訓練、迴圈執行、
統計計算、報表輸出與圖表產生。

支援兩種模式：
    - **單股模式** (run_single): 單一股票、單一日期。
    - **批次模式** (run_batch): 多支股票批次回測。
"""

from __future__ import annotations

import logging
import os
from typing import List, Optional

from analytics.vwap_report_generator import VWAPReportGenerator
from analytics.vwap_statistics import VWAPStatistics
from core.vwap_loop import VWAPLoop
from exporters.vwap_csv_exporter import VWAPCsvExporter
from strategy_modules.data_processor import DataProcessor
from strategy_modules.vwap_config_loader import VWAPConfig
from strategy_modules.vwap_models import VWAPMetrics
from strategy_modules.volume_profile import VolumeProfilePredictor
from visualization.vwap_chart_creator import VWAPChartCreator

logger = logging.getLogger(__name__)

_DEFAULT_DATA_DIR = "D:/feature_data/feature"


class VWAPEngine:
    """VWAP 執行策略回測引擎。"""

    def __init__(self, config_path: str) -> None:
        self.config = VWAPConfig(config_path)

    # ------------------------------------------------------------------
    # 單股回測
    # ------------------------------------------------------------------

    def run_single(
        self,
        stock_id: str,
        date: str,
        no_csv: bool = False,
        no_chart: bool = False,
        silent: bool = False,
    ) -> Optional[VWAPMetrics]:
        """針對單一股票在單一日期執行 Adaptive VWAP 回測。"""

        vwap_cfg = self.config.get_vwap_config()
        data_processor = DataProcessor(vwap_cfg)

        # 建立輸出目錄
        output_dir = os.path.join(self.config.output_path, date)
        os.makedirs(output_dir, exist_ok=True)

        # 1. 載入資料
        df = data_processor.load_and_prepare_vwap_data(stock_id, date)
        if df.empty:
            logger.error("無法載入 %s 在 %s 的資料", stock_id, date)
            return None

        # 2. 取得參考價
        ref_price = data_processor.get_reference_price(
            stock_id, date, self.config.close_prices_file
        )
        if ref_price is None or (isinstance(ref_price, float) and ref_price != ref_price):
            logger.warning("無法取得 %s 的昨收價，使用第一筆成交價", stock_id)
            ref_price = 0.0

        # 3. 訓練 Volume Profile 模型
        if vwap_cfg.get("adaptive_enabled", True):
            predictor = self._train_volume_profile(stock_id, date, vwap_cfg)
        else:
            logger.info("%s 使用 TWAP fallback（adaptive 已停用）", stock_id)
            predictor = VolumeProfilePredictor._default_predictor()

        # 4. 執行回測迴圈
        loop = VWAPLoop(vwap_cfg, predictor)
        state = loop.run(df, stock_id, ref_price)

        # 5. 檢查成交
        if len(state.orders) == 0:
            logger.warning("%s 在 %s 無任何成交", stock_id, date)
            return None

        # 6. 計算績效統計
        metrics = VWAPStatistics.calculate(
            state, stock_id, date,
            volume_prediction_r2=predictor.r_squared,
        )

        # 7. 報告
        if not silent:
            company_name = data_processor.get_company_name(stock_id)
            VWAPReportGenerator.print_report(metrics, company_name)

        # 8. CSV
        if not no_csv:
            VWAPCsvExporter.export_orders(state, stock_id, date, output_dir)

        # 9. 圖表
        if not no_chart:
            company_name = data_processor.get_company_name(stock_id)
            VWAPChartCreator.create_chart(
                state, metrics, stock_id, date, company_name,
                output_dir,
                self.config.generate_html,
                self.config.generate_png,
            )

        return metrics

    # ------------------------------------------------------------------
    # 批次回測
    # ------------------------------------------------------------------

    def run_batch(
        self,
        stock_list: List[str],
        date: str,
        no_csv: bool = False,
        no_chart: bool = False,
    ) -> List[VWAPMetrics]:
        """多支股票批次回測。"""
        results: List[VWAPMetrics] = []

        for i, stock_id in enumerate(stock_list):
            logger.info(
                "=== 處理 %s (%d/%d) ===", stock_id, i + 1, len(stock_list)
            )
            metrics = self.run_single(
                stock_id, date, no_csv=no_csv, no_chart=no_chart
            )
            if metrics is not None:
                results.append(metrics)

        if results and not no_csv:
            output_dir = os.path.join(self.config.output_path, date)
            os.makedirs(output_dir, exist_ok=True)
            VWAPCsvExporter.export_summary(results, date, output_dir)

        logger.info(
            "批次完成: %d/%d 支股票成功", len(results), len(stock_list)
        )
        return results

    # ------------------------------------------------------------------
    # Volume Profile 訓練
    # ------------------------------------------------------------------

    def _train_volume_profile(
        self,
        stock_id: str,
        date: str,
        vwap_cfg: dict,
    ) -> VolumeProfilePredictor:
        """訓練 Volume Profile 迴歸模型。"""
        lookback = vwap_cfg.get("volume_lookback_days", 20)
        history_dates = self._get_training_dates(stock_id, date, lookback)

        if not history_dates:
            logger.warning(
                "%s 無歷史訓練資料，使用預設模型", stock_id
            )
            return VolumeProfilePredictor._default_predictor()

        logger.info(
            "%s 訓練 volume profile: %d 天 (%s ~ %s)",
            stock_id, len(history_dates),
            history_dates[0], history_dates[-1],
        )

        return VolumeProfilePredictor.train_from_history(
            stock_id, history_dates, _DEFAULT_DATA_DIR
        )

    @staticmethod
    def _get_training_dates(
        stock_id: str,
        date: str,
        lookback: int = 20,
    ) -> List[str]:
        """取得該股票在 date 之前有資料的最近 lookback 天日期列表。"""
        import glob
        from datetime import datetime as dt

        target_date = dt.strptime(date, "%Y-%m-%d")

        # 搜索所有有該股票資料的日期目錄
        pattern = os.path.join(_DEFAULT_DATA_DIR, "*", f"{stock_id}.parquet")
        matches = glob.glob(pattern)

        available_dates: List[str] = []
        for path in matches:
            # 從路徑中提取日期: .../feature/2025-01-06/2317.parquet
            dir_name = os.path.basename(os.path.dirname(path))
            try:
                d = dt.strptime(dir_name, "%Y-%m-%d")
                if d < target_date:
                    available_dates.append(dir_name)
            except ValueError:
                continue

        # 排序後取最近 lookback 天
        available_dates.sort()
        return available_dates[-lookback:]
