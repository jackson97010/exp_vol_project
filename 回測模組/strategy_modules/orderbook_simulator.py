"""
委託簿模擬器模組

模擬台灣股市五檔委託簿，用於 VWAP 執行策略回測中的
可用深度估算、成交價格模擬與滑價計算。

由於原始 parquet 資料僅包含五檔量（bid1_volume ~ bid5_volume、
ask1_volume ~ ask5_volume），不含實際委買/委賣價格，因此
以最近成交價作為中間價的近似值。
"""

import math
import logging
from typing import List

logger = logging.getLogger(__name__)

# 五檔欄位名稱常數
_BID_COLUMNS: List[str] = [f"bid{i}_volume" for i in range(1, 6)]
_ASK_COLUMNS: List[str] = [f"ask{i}_volume" for i in range(1, 6)]


class OrderBookSimulator:
    """五檔委託簿模擬器。

    根據 Depth 快照更新五檔委買/委賣量，並提供深度查詢、
    中間價估算與模擬成交價計算等功能。

    Attributes:
        bid_volumes: 委買一~五檔量（lots/張）。
        ask_volumes: 委賣一~五檔量（lots/張）。
        last_trade_price: 最近一筆成交價。
        has_depth: 是否已收到至少一次 Depth 更新。
    """

    def __init__(self) -> None:
        self.bid_volumes: List[float] = [0.0] * 5
        self.ask_volumes: List[float] = [0.0] * 5
        self.last_trade_price: float = 0.0
        self.has_depth: bool = False

    # ------------------------------------------------------------------
    # 狀態更新
    # ------------------------------------------------------------------

    def update_from_depth(self, row: object) -> None:
        """從 Depth 記錄更新五檔委買/委賣量。

        ``row`` 通常來自 ``df.itertuples()``，以 ``getattr`` 安全讀取
        各檔位量欄位。遇到欄位不存在或值為 NaN 的情況一律視為 0。

        Args:
            row: 一筆 namedtuple 格式的 Depth 資料列。
        """
        for i, col in enumerate(_BID_COLUMNS):
            value = getattr(row, col, None)
            self.bid_volumes[i] = 0.0 if value is None or _is_nan(value) else float(value)

        for i, col in enumerate(_ASK_COLUMNS):
            value = getattr(row, col, None)
            self.ask_volumes[i] = 0.0 if value is None or _is_nan(value) else float(value)

        self.has_depth = True

    def update_from_trade(self, price: float) -> None:
        """更新最近成交價。

        Args:
            price: 最新成交價格。
        """
        self.last_trade_price = price

    # ------------------------------------------------------------------
    # 升降單位
    # ------------------------------------------------------------------

    @staticmethod
    def get_tick_size(price: float) -> float:
        """根據台灣證交所規則回傳該價位的升降單位。

        價格區間與升降單位對照：
            - price >= 1000 -> 5
            - price >= 500  -> 1
            - price >= 100  -> 0.5
            - price >= 50   -> 0.1
            - price >= 10   -> 0.05
            - price < 10    -> 0.01

        Args:
            price: 目前價格。

        Returns:
            對應的升降單位（float）。
        """
        tick_rules = [
            (1000, 5.0),
            (500, 1.0),
            (100, 0.5),
            (50, 0.1),
            (10, 0.05),
        ]
        for threshold, tick in tick_rules:
            if price >= threshold:
                return tick
        return 0.01

    # ------------------------------------------------------------------
    # 價格查詢
    # ------------------------------------------------------------------

    def get_mid_price(self) -> float:
        """回傳估計的中間價。

        由於原始資料不含實際委買/委賣價格，以最近成交價作為
        中間價的近似值。

        Returns:
            估計中間價（即 ``last_trade_price``）。
        """
        return self.last_trade_price

    # ------------------------------------------------------------------
    # 深度查詢
    # ------------------------------------------------------------------

    def get_available_ask_depth(self, spread_or_levels: float | int = 3) -> float:
        """計算委賣方可用的張數。

        支援兩種模式：
            - **整數** (n_levels): 直接回傳 ask1 ~ ask{n_levels} 的量加總。
              這是推薦的方式，因為台股 tick size 在不同價位差異極大，
              用固定比例換算檔位數不直覺。
            - **浮點數** (spread): 舊式比例模式，向下相容。
              計算 spread 涵蓋的檔位數：
              ``n_levels = min(floor(mid_price * spread / tick_size) + 1, 5)``

        若尚未收到 Depth 資料，回傳 0。

        Args:
            spread_or_levels: 整數表示檔位數 (1~5)，
                浮點數表示價差容忍度比例 (e.g. 0.005 = 0.5%)。

        Returns:
            可用的委賣深度（lots/張）。
        """
        if not self.has_depth:
            return 0.0

        # 整數 → 直接用檔位數
        if isinstance(spread_or_levels, int):
            n_levels = max(1, min(spread_or_levels, 5))
            return sum(self.ask_volumes[:n_levels])

        # 浮點數 → 舊式比例換算
        spread = spread_or_levels
        mid_price = self.get_mid_price()
        if mid_price <= 0:
            logger.debug("中間價 <= 0，無法計算可用深度")
            return 0.0

        tick_size = self.get_tick_size(mid_price)
        if tick_size <= 0:
            logger.warning("升降單位 <= 0，回傳 ask1 量")
            return self.ask_volumes[0]

        n_levels = math.floor(mid_price * spread / tick_size) + 1
        n_levels = max(n_levels, 1)
        n_levels = min(n_levels, 5)

        return sum(self.ask_volumes[:n_levels])

    def get_total_ask_depth(self) -> float:
        """回傳委賣方五檔總量。

        Returns:
            委賣五檔量加總（lots/張）。
        """
        return sum(self.ask_volumes)

    def get_total_bid_depth(self) -> float:
        """回傳委買方五檔總量。

        Returns:
            委買五檔量加總（lots/張）。
        """
        return sum(self.bid_volumes)

    # ------------------------------------------------------------------
    # 成交價模擬
    # ------------------------------------------------------------------

    def get_fill_price(self, slippage_model: str = "mid") -> float:
        """計算模擬成交價。

        Args:
            slippage_model: 滑價模型，可選值：
                - ``"mid"``: 以中間價成交（即 last_trade_price）。
                - ``"ask1"``: 以 ask1 價位成交，模擬為中間價加上
                  一個升降單位（mid_price + tick_size）。
                - ``"taker"``: 主動吃賣方，成交價 = mid + tick。
                  等同 ask1，語意上更明確（Adaptive VWAP 使用）。
                - ``"maker"``: 掛買方等成交，成交價 = mid。
                  假設以最後成交價成交（保守估計）。

        Returns:
            模擬成交價。

        Raises:
            ValueError: 若 slippage_model 不是已知的值。
        """
        mid_price = self.get_mid_price()

        if slippage_model == "mid" or slippage_model == "maker":
            return mid_price

        if slippage_model in ("ask1", "taker"):
            tick_size = self.get_tick_size(mid_price)
            return mid_price + tick_size

        raise ValueError(
            f"不支援的滑價模型: {slippage_model!r}，"
            f"僅支援 'mid', 'ask1', 'taker', 'maker'"
        )

    # ------------------------------------------------------------------
    # 除錯用字串表示
    # ------------------------------------------------------------------

    def __repr__(self) -> str:
        return (
            f"OrderBookSimulator("
            f"last_trade_price={self.last_trade_price}, "
            f"has_depth={self.has_depth}, "
            f"bid_total={self.get_total_bid_depth():.1f}, "
            f"ask_total={self.get_total_ask_depth():.1f})"
        )


# ----------------------------------------------------------------------
# 模組層級輔助函式
# ----------------------------------------------------------------------

def _is_nan(value: object) -> bool:
    """安全地判斷值是否為 NaN。

    支援 float NaN 以及任何實作 ``__float__`` 的物件。
    非數值型別一律回傳 False（不視為 NaN）。

    Args:
        value: 要檢查的值。

    Returns:
        若為 NaN 則回傳 True，否則 False。
    """
    try:
        return math.isnan(float(value))
    except (TypeError, ValueError):
        return False
