"""VWAP 委託量計算模組 (Adaptive 版)

根據 Volume Profile 預測器計算的 child order volume 做 min/max 限制，
取代舊版固定比例參與率邏輯。

台灣股市慣例：1 張 (lot) = 1000 股。
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


class VWAPOrderLogic:
    """VWAP Adaptive 委託量計算器。

    根據 VolumeProfilePredictor 計算出的子委託量，
    進行 min/max 限制後回傳最終委託張數。

    Attributes:
        min_order_lots: 單筆最小委託張數。
        max_single_order_lots: 單筆最大委託張數。
    """

    def __init__(self, config: dict) -> None:
        self.min_order_lots: int = config["min_order_lots"]
        self.max_single_order_lots: int = config["max_single_order_lots"]

        logger.info(
            "VWAPOrderLogic 初始化 (adaptive): "
            "min_order_lots=%d, max_single_order_lots=%d",
            self.min_order_lots,
            self.max_single_order_lots,
        )

    def calculate_adaptive_order_size(
        self,
        child_volume: float,
        volume_remaining: float,
    ) -> float:
        """根據 volume profile 計算的 child_volume 做 min/max 限制。

        限制邏輯：
            1. 不超過 volume_remaining（避免超買）。
            2. 不超過 max_single_order_lots。
            3. 若 < min_order_lots 則回傳 0（跳過本分鐘）。

        Args:
            child_volume: 由 VolumeProfilePredictor.calculate_child_order_volume()
                算出的該分鐘理想委託量。
            volume_remaining: 我們剩餘要買的量。

        Returns:
            委託張數 (float)。回傳 0 表示本分鐘不下單。
        """
        if volume_remaining <= 0 or child_volume <= 0:
            return 0.0

        order_lots = min(child_volume, volume_remaining, self.max_single_order_lots)

        if order_lots < self.min_order_lots:
            # 特殊情況：剩餘量小於最小下單量時，允許收尾下單
            if volume_remaining <= self.min_order_lots:
                return volume_remaining
            return 0.0

        return order_lots
