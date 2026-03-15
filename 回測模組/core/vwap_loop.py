"""VWAP 執行策略回測迴圈模組 (Adaptive 版)

根據 CMRI 論文改為逐分鐘排程：
    1. 每分鐘計算市場成交量 delta_market。
    2. 用 VolumeProfilePredictor 預測全日量 V_hat。
    3. 用論文公式(3) 計算 child order volume。
    4. 用 MOLOAnalyzer 決定 taker/maker。
    5. OrderBookSimulator 模擬成交價。

台灣股市慣例：
    - 1 張 (lot) = 1000 股
    - 金額 (amount) = 價格 * 張數 * 1000
    - 成交量單位為張
"""

from __future__ import annotations

import logging
from datetime import datetime, time as dt_time
from typing import Optional

import pandas as pd

from strategy_modules.vwap_models import VWAPOrder, VWAPExecutionState
from strategy_modules.orderbook_simulator import OrderBookSimulator
from strategy_modules.vwap_order_logic import VWAPOrderLogic
from strategy_modules.volume_profile import VolumeProfilePredictor
from strategy_modules.mo_lo_analyzer import MOLOAnalyzer

logger = logging.getLogger(__name__)

_LOG_INTERVAL: int = 10

# 台股交易時段：09:00 ~ 13:30 = 270 分鐘
_MARKET_OPEN = dt_time(9, 0, 0)
_TOTAL_TRADING_MINUTES: int = 270


class VWAPLoop:
    """VWAP Adaptive 執行策略的回測主迴圈。

    逐分鐘跟市場量走，根據 volume profile 預測動態計算
    每分鐘的子委託量。
    """

    def __init__(
        self,
        config: dict,
        volume_predictor: VolumeProfilePredictor,
    ) -> None:
        self.config = config
        self.predictor = volume_predictor
        self.mo_lo_analyzer = MOLOAnalyzer(
            lookback_ticks=config.get("mo_lo_lookback_ticks", 100)
        )
        self.order_logic = VWAPOrderLogic(config)

    def run(
        self,
        df: pd.DataFrame,
        stock_id: str,
        ref_price: float,
    ) -> VWAPExecutionState:
        """執行 Adaptive VWAP 回測模擬。"""

        if not self.config.get("is_buy", True):
            raise NotImplementedError(
                "Adaptive VWAP 目前僅支援買入 (is_buy=true)，"
                "賣出邏輯尚未實作。"
            )

        state = VWAPExecutionState()
        orderbook = OrderBookSimulator()

        # 設定參數
        start_time: dt_time = self.config["start_time"]
        end_time: dt_time = self.config["end_time"]
        total_volume_quote: float = self.config["total_volume_quote"]
        ato_end_time: dt_time = self.config.get("ato_end_time", dt_time(9, 5, 0))
        order_start_time: dt_time = self.config.get("order_start_time", dt_time(9, 10, 0))
        mo_lo_threshold: float = self.config.get("mo_lo_threshold", 0.5)
        price_spread_levels: int = self.config.get("price_spread_levels", 3)

        # 逐分鐘追蹤
        current_minute: Optional[pd.Timestamp] = None
        minute_market_vol: float = 0.0
        prev_cum_market_vol: float = 0.0
        cum_market_vol: float = 0.0
        v_ato: float = 0.0
        q_remaining: float = 0.0

        logger.info(
            "[%s] Adaptive VWAP 回測開始: 資料筆數=%d, 前收價=%.2f, "
            "窗口=%s~%s, 目標金額=%.0f, R2=%.4f",
            stock_id, len(df), ref_price,
            start_time, end_time, total_volume_quote,
            self.predictor.r_squared,
        )

        for row in df.itertuples(index=False):
            current_time = row.time
            row_type = getattr(row, "type", "Trade")

            # ----------------------------------------------------------
            # Depth 記錄：更新委託簿，紀錄深度快照
            # ----------------------------------------------------------
            if row_type == "Depth":
                orderbook.update_from_depth(row)
                bid_depth = orderbook.get_total_bid_depth()
                ask_depth = orderbook.get_total_ask_depth()
                state.market_bid_depth.append((current_time, bid_depth))
                state.market_ask_depth.append((current_time, ask_depth))
                continue

            # ----------------------------------------------------------
            # Trade 記錄
            # ----------------------------------------------------------
            trade_price = float(row.price)
            trade_volume = float(row.volume)

            orderbook.update_from_trade(trade_price)
            state.market_prices.append((current_time, trade_price))

            # 更新 MO/LO 分析器
            tick_type_val = str(getattr(row, "tick_type", "1"))
            self.mo_lo_analyzer.update(tick_type_val, trade_volume)

            # 時間窗口檢查
            tick_time = _extract_time(current_time)
            if not (start_time <= tick_time <= end_time):
                continue

            # 累積 ATO 量 (09:00 ~ ato_end_time)
            if tick_time < ato_end_time:
                v_ato += trade_volume

            # 累積市場量
            cum_market_vol += trade_volume
            state.market_total_volume += trade_volume
            state.market_total_amount += trade_price * trade_volume * 1000

            # 初始化起始價與目標量
            if state.start_price == 0 and trade_price > 0:
                state.start_price = trade_price
                state.target_lots = total_volume_quote / (trade_price * 1000)
                q_remaining = state.target_lots
                state.volume_remaining = q_remaining
                logger.info(
                    "[%s] 起始價=%.2f, 目標張數=%.2f, 目標金額=%.0f",
                    stock_id, trade_price, state.target_lots, total_volume_quote,
                )

            if q_remaining <= 0:
                continue

            # 尚未到下單開始時間，只累積市場量
            if tick_time < order_start_time:
                # 累積分鐘量（即使還沒開始下單也要追蹤）
                tick_minute = _floor_minute(current_time)
                if tick_minute != current_minute:
                    prev_cum_market_vol = cum_market_vol - trade_volume
                    minute_market_vol = 0.0
                    current_minute = tick_minute
                minute_market_vol += trade_volume
                continue

            # ===========================================================
            # 關鍵：分鐘切換時計算下單量
            # ===========================================================
            tick_minute = _floor_minute(current_time)

            if tick_minute != current_minute:
                if current_minute is not None and tick_time >= order_start_time:
                    # 上一分鐘結束 → 計算 child order
                    order = self._execute_minute_order(
                        state=state,
                        orderbook=orderbook,
                        stock_id=stock_id,
                        current_time=current_time,
                        minute_market_vol=minute_market_vol,
                        cum_market_vol=cum_market_vol - trade_volume,
                        prev_cum_market_vol=prev_cum_market_vol,
                        v_ato=v_ato,
                        q_remaining=q_remaining,
                        mo_lo_threshold=mo_lo_threshold,
                        price_spread_levels=price_spread_levels,
                    )

                    if order is not None:
                        state.add_fill(order)
                        q_remaining -= order.fill_lots

                        if order.order_id % _LOG_INTERVAL == 0:
                            logger.info(
                                "[%s] 第%d筆: 價=%.2f, 量=%.1f張, "
                                "VWAP=%.2f, 完成=%.1f%%, 類型=%s",
                                stock_id, order.order_id,
                                order.fill_price, order.fill_lots,
                                state.current_vwap, state.delta * 100,
                                order.order_type,
                            )

                # 重置分鐘計數器
                prev_cum_market_vol = cum_market_vol - trade_volume
                minute_market_vol = 0.0
                current_minute = tick_minute

            minute_market_vol += trade_volume

        # ------------------------------------------------------------------
        # 最後一分鐘收尾
        # ------------------------------------------------------------------
        if q_remaining > 0 and minute_market_vol > 0 and current_minute is not None:
            order = self._execute_minute_order(
                state=state,
                orderbook=orderbook,
                stock_id=stock_id,
                current_time=current_time,
                minute_market_vol=minute_market_vol,
                cum_market_vol=cum_market_vol,
                prev_cum_market_vol=prev_cum_market_vol,
                v_ato=v_ato,
                q_remaining=q_remaining,
                mo_lo_threshold=mo_lo_threshold,
                price_spread_levels=price_spread_levels,
            )
            if order is not None:
                state.add_fill(order)
                q_remaining -= order.fill_lots

        state.finalize()

        logger.info(
            "[%s] 執行完畢: 成交%d筆, 總張數=%.2f/%.2f, "
            "VWAP=%.2f, 完成率=%.1f%%",
            stock_id, len(state.orders),
            state.total_filled_lots, state.target_lots,
            state.current_vwap, state.delta * 100,
        )

        return state

    # ------------------------------------------------------------------
    # 分鐘級下單邏輯
    # ------------------------------------------------------------------

    def _execute_minute_order(
        self,
        *,
        state: VWAPExecutionState,
        orderbook: OrderBookSimulator,
        stock_id: str,
        current_time: datetime,
        minute_market_vol: float,
        cum_market_vol: float,
        prev_cum_market_vol: float,
        v_ato: float,
        q_remaining: float,
        mo_lo_threshold: float,
        price_spread_levels: int,
    ) -> Optional[VWAPOrder]:
        """執行一分鐘的下單邏輯，回傳 VWAPOrder 或 None。"""

        if q_remaining <= 0 or minute_market_vol <= 0:
            return None

        # 計算 elapsed_ratio
        minutes_from_open = _minutes_since(current_time, _MARKET_OPEN)
        elapsed_ratio = min(minutes_from_open / _TOTAL_TRADING_MINUTES, 1.0)

        # 預測全日量
        v_hat = self.predictor.predict_total(v_ato, cum_market_vol, elapsed_ratio)

        # 論文公式(3)：child order volume
        child_vol = self.predictor.calculate_child_order_volume(
            delta_market=minute_market_vol,
            v_hat=v_hat,
            v_prev=prev_cum_market_vol,
            q_remaining=q_remaining,
        )

        # min/max 限制
        order_lots = self.order_logic.calculate_adaptive_order_size(
            child_vol, q_remaining
        )

        if order_lots <= 0:
            return None

        # 決定 MO/LO
        order_type = self.mo_lo_analyzer.decide_order_type(mo_lo_threshold)
        mo_ratio = self.mo_lo_analyzer.get_mo_ratio_buy()

        # 模擬成交
        mid_price = orderbook.get_mid_price()
        fill_price = orderbook.get_fill_price(order_type)
        fill_amount = fill_price * order_lots * 1000
        ask_depth = orderbook.get_available_ask_depth(price_spread_levels)

        order_id = len(state.orders) + 1

        new_cumulative_lots = state.total_filled_lots + order_lots
        new_cumulative_amount = state.total_spent_amount + fill_amount

        return VWAPOrder(
            order_id=order_id,
            time=current_time,
            fill_price=fill_price,
            fill_lots=order_lots,
            fill_amount_twd=fill_amount,
            mid_price=mid_price,
            ask_depth_available=ask_depth,
            cumulative_lots=new_cumulative_lots,
            cumulative_amount=new_cumulative_amount,
            delta=(
                new_cumulative_lots / state.target_lots
                if state.target_lots > 0 else 0.0
            ),
            running_vwap=(
                new_cumulative_amount / (new_cumulative_lots * 1000)
                if new_cumulative_lots > 0 else fill_price
            ),
            order_type=order_type,
            mo_ratio_at_fill=mo_ratio,
            minute_market_vol=minute_market_vol,
            predicted_total_vol=v_hat,
        )


# ----------------------------------------------------------------------
# 模組層級輔助函式
# ----------------------------------------------------------------------


def _extract_time(timestamp: object) -> dt_time:
    """從各種時間型別中安全地提取 datetime.time。"""
    if isinstance(timestamp, dt_time):
        return timestamp
    time_attr = getattr(timestamp, "time", None)
    if callable(time_attr):
        return time_attr()
    raise TypeError(
        f"無法從 {type(timestamp).__name__} 提取 datetime.time，"
        f"值={timestamp!r}"
    )


def _floor_minute(timestamp: object) -> pd.Timestamp:
    """將時間戳記取整到分鐘。"""
    ts = pd.Timestamp(timestamp)
    return ts.floor("1min")


def _minutes_since(timestamp: object, ref_time: dt_time) -> float:
    """計算 timestamp 距離 ref_time 的分鐘數。"""
    t = _extract_time(timestamp)
    return (
        (t.hour - ref_time.hour) * 60
        + (t.minute - ref_time.minute)
        + (t.second - ref_time.second) / 60.0
    )
