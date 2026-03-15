"""VWAP execution result CSV exporter (Adaptive 版).

Exports order details and batch summary with adaptive strategy fields.
"""

from __future__ import annotations

import csv
import logging
import os
from typing import List

from strategy_modules.vwap_models import VWAPExecutionState, VWAPMetrics

logger = logging.getLogger(__name__)


class VWAPCsvExporter:
    """VWAP 執行結果 CSV 輸出器"""

    @staticmethod
    def export_orders(
        state: VWAPExecutionState,
        stock_id: str,
        date: str,
        output_dir: str,
    ) -> str:
        """Export order detail CSV with adaptive fields."""
        os.makedirs(output_dir, exist_ok=True)

        filename = f"{stock_id}_vwap_orders_{date}.csv"
        filepath = os.path.join(output_dir, filename)

        headers = [
            "訂單編號",
            "時間",
            "成交價",
            "成交張數",
            "成交金額",
            "中間價",
            "賣方掛單量",
            "累計張數",
            "累計金額",
            "完成比例",
            "即時VWAP",
            "委託類型",
            "MO比例",
            "分鐘市場量",
            "預測全日量",
        ]

        with open(filepath, "w", newline="", encoding="utf-8-sig") as fh:
            writer = csv.writer(fh)
            writer.writerow(headers)

            for order in state.orders:
                writer.writerow(
                    [
                        order.order_id,
                        order.time.strftime("%H:%M:%S.%f"),
                        order.fill_price,
                        order.fill_lots,
                        order.fill_amount_twd,
                        order.mid_price,
                        order.ask_depth_available,
                        order.cumulative_lots,
                        order.cumulative_amount,
                        f"{order.delta * 100:.1f}%",
                        order.running_vwap,
                        order.order_type,
                        f"{order.mo_ratio_at_fill:.3f}",
                        f"{order.minute_market_vol:.1f}",
                        f"{order.predicted_total_vol:.1f}",
                    ]
                )

        logger.info("VWAP order detail CSV written to %s", filepath)
        return filepath

    @staticmethod
    def export_summary(
        metrics_list: List[VWAPMetrics],
        date: str,
        output_dir: str,
    ) -> str:
        """Export batch summary CSV with adaptive fields."""
        os.makedirs(output_dir, exist_ok=True)

        filename = f"vwap_summary_{date}.csv"
        filepath = os.path.join(output_dir, filename)

        headers = [
            "股票代碼",
            "日期",
            "目標張數",
            "完成張數",
            "完成率",
            "目標金額",
            "實際金額",
            "VWAP價格",
            "起始價",
            "市場VWAP",
            "滑價(bps)",
            "總下單次數",
            "參與率",
            "平均MO比例",
            "Taker次數",
            "Maker次數",
            "量預測R2",
        ]

        with open(filepath, "w", newline="", encoding="utf-8-sig") as fh:
            writer = csv.writer(fh)
            writer.writerow(headers)

            for m in metrics_list:
                target_amount = m.target_lots * m.start_price * 1000
                writer.writerow(
                    [
                        m.stock_id,
                        m.date,
                        m.target_lots,
                        m.total_filled_lots,
                        f"{m.completion_ratio * 100:.1f}%",
                        target_amount,
                        m.total_spent_amount,
                        m.vwap_price,
                        m.start_price,
                        m.market_vwap,
                        m.slippage_bps_vs_market,
                        m.total_orders,
                        f"{m.participation_rate * 100:.2f}%",
                        f"{m.avg_mo_ratio:.3f}",
                        m.taker_order_count,
                        m.maker_order_count,
                        f"{m.volume_prediction_r2:.4f}",
                    ]
                )

        logger.info("VWAP summary CSV written to %s", filepath)
        return filepath
