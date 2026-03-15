"""VWAP 執行策略統計計算模組

根據已完成的 VWAP 執行狀態計算最終績效指標，
包含 Adaptive VWAP 的 MO/LO 比例與量預測準確度等指標。
"""

from __future__ import annotations

import logging

from strategy_modules.vwap_models import VWAPExecutionState, VWAPMetrics

logger = logging.getLogger(__name__)


class VWAPStatistics:
    """VWAP 執行策略統計計算器。"""

    @staticmethod
    def calculate(
        state: VWAPExecutionState,
        stock_id: str,
        date: str,
        volume_prediction_r2: float = 0.0,
    ) -> VWAPMetrics:
        """根據執行狀態計算最終績效統計。"""

        # --- 執行 VWAP 價格 ---
        if state.total_filled_lots > 0:
            vwap_price = state.total_spent_amount / (state.total_filled_lots * 1000)
        else:
            vwap_price = 0.0

        # --- 市場 VWAP 價格 ---
        if state.market_total_volume > 0:
            market_vwap = state.market_total_amount / (
                state.market_total_volume * 1000
            )
        else:
            market_vwap = 0.0

        # --- 相對開盤價滑價 (bps) ---
        if state.start_price > 0:
            slippage_bps_vs_start = (
                (vwap_price - state.start_price) / state.start_price * 10000
            )
        else:
            slippage_bps_vs_start = 0.0

        # --- 相對市場 VWAP 滑價 (bps) ---
        if market_vwap > 0:
            slippage_bps_vs_market = (
                (vwap_price - market_vwap) / market_vwap * 10000
            )
        else:
            slippage_bps_vs_market = 0.0

        # --- 完成率 ---
        if state.target_lots > 0:
            completion_ratio = state.total_filled_lots / state.target_lots
        else:
            completion_ratio = 0.0

        # --- 市場參與率 ---
        if state.market_total_volume > 0:
            participation_rate = (
                state.total_filled_lots / state.market_total_volume
            )
        else:
            participation_rate = 0.0

        # --- 委託筆數 ---
        total_orders = len(state.orders)

        # --- 執行時長 ---
        if total_orders >= 2:
            first_time = state.orders[0].time
            last_time = state.orders[-1].time
            duration_delta = last_time - first_time
            execution_duration_minutes = duration_delta.total_seconds() / 60.0
        else:
            execution_duration_minutes = 0.0

        # --- Adaptive 指標 ---
        taker_count = sum(1 for o in state.orders if o.order_type == "taker")
        maker_count = sum(1 for o in state.orders if o.order_type == "maker")

        if total_orders > 0:
            avg_mo_ratio = sum(o.mo_ratio_at_fill for o in state.orders) / total_orders
        else:
            avg_mo_ratio = 0.0

        logger.info(
            "%s %s 統計完成: VWAP=%.2f, 市場VWAP=%.2f, "
            "滑價(vs市場)=%.2f bps, 完成率=%.2f%%, "
            "taker=%d, maker=%d, avg_MO=%.3f, R2=%.4f",
            stock_id, date, vwap_price, market_vwap,
            slippage_bps_vs_market, completion_ratio * 100,
            taker_count, maker_count, avg_mo_ratio, volume_prediction_r2,
        )

        return VWAPMetrics(
            stock_id=stock_id,
            date=date,
            vwap_price=vwap_price,
            market_vwap=market_vwap,
            start_price=state.start_price,
            slippage_bps_vs_start=slippage_bps_vs_start,
            slippage_bps_vs_market=slippage_bps_vs_market,
            target_lots=state.target_lots,
            total_filled_lots=state.total_filled_lots,
            completion_ratio=completion_ratio,
            total_spent_amount=state.total_spent_amount,
            participation_rate=participation_rate,
            total_orders=total_orders,
            execution_duration_minutes=execution_duration_minutes,
            avg_mo_ratio=avg_mo_ratio,
            taker_order_count=taker_count,
            maker_order_count=maker_count,
            volume_prediction_r2=volume_prediction_r2,
        )
