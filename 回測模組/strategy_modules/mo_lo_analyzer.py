"""MO/LO 比例分析器模組

實作 CMRI 論文《An Adaptive Order Execution Strategy for VWAP Tracking》
中的公式 (5)(6)，根據 tick_type 動態計算 Market Order (MO) 比例，
並據此決定使用 taker (主動成交) 或 maker (被動掛單) 模式。

台灣股市 tick_type 定義：
    - '1' (外盤): 成交價 >= 賣方掛價 → 主動買 (Market Order Buy)
    - '2' (內盤): 成交價 <= 買方掛價 → 主動賣 (Market Order Sell)

MO ratio 高 → 市場搶著買，我們也用 MO (taker) 確保成交
MO ratio 低 → 市場冷靜，我們用 LO (maker) 等更好的價
"""

from __future__ import annotations

import logging
from collections import deque
from typing import Deque, Tuple

logger = logging.getLogger(__name__)


class MOLOAnalyzer:
    """MO/LO 比例分析器。

    維護最近 N 筆成交的 tick_type 紀錄，滾動計算
    Market Order Buy 的量占比。

    Attributes:
        lookback_ticks: 滾動窗口大小（筆數）。
    """

    def __init__(self, lookback_ticks: int = 100) -> None:
        self.lookback_ticks = lookback_ticks
        self._history: Deque[Tuple[str, float]] = deque(maxlen=lookback_ticks)
        self._mo_buy_vol: float = 0.0   # 窗口內外盤量累計
        self._total_vol: float = 0.0    # 窗口內總量累計

    def update(self, tick_type: str, volume: float) -> None:
        """記錄一筆成交的 tick_type 與成交量。

        Args:
            tick_type: 成交類型，'1' = 外盤(MO buy), '2' = 內盤(MO sell)。
            volume: 成交量（張）。
        """
        is_mo_buy = tick_type == '1'

        # 若 deque 已滿，移除最舊的一筆
        if len(self._history) == self.lookback_ticks:
            old_type, old_vol = self._history[0]
            self._total_vol -= old_vol
            if old_type == '1':
                self._mo_buy_vol -= old_vol

        self._history.append((tick_type, volume))
        self._total_vol += volume
        if is_mo_buy:
            self._mo_buy_vol += volume

    def get_mo_ratio_buy(self) -> float:
        """計算當前窗口的 MO buy 量比例。

        論文公式 (5)：r_MO_buy = vol(tick_type=='1') / vol_total

        Returns:
            MO buy ratio (0.0 ~ 1.0)。無資料時回傳 0.5（中性）。
        """
        if self._total_vol <= 0:
            return 0.5
        return self._mo_buy_vol / self._total_vol

    def decide_order_type(self, threshold: float = 0.5) -> str:
        """根據 MO ratio 決定委託類型。

        MO ratio > threshold → 'taker' (主動吃賣方，確保成交)
        MO ratio <= threshold → 'maker' (掛買方等成交，成本較低)

        Args:
            threshold: MO ratio 門檻值。

        Returns:
            'taker' 或 'maker'。
        """
        return 'taker' if self.get_mo_ratio_buy() > threshold else 'maker'

    def reset(self) -> None:
        """清除所有歷史紀錄。"""
        self._history.clear()
        self._mo_buy_vol = 0.0
        self._total_vol = 0.0
