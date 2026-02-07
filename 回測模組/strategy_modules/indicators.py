"""
技術指標模組
包含各種技術指標計算器
"""

import pandas as pd
from collections import deque
from datetime import datetime, timedelta
from typing import Optional, Tuple
import logging

logger = logging.getLogger(__name__)


class DayHighMomentumTracker:
    """Day High 動能追蹤器"""

    def __init__(self, window_seconds: int = 60):
        """
        初始化動能追蹤器

        Args:
            window_seconds: 追蹤窗口時間（秒）
        """
        self.window_seconds = window_seconds
        self.day_high_history = deque()  # 保存時間戳記和 day_high
        self.current_day_high = 0.0
        self.day_high_1min_ago = 0.0
        self.growth_rate_history = deque(maxlen=20)
        self.last_growth_rate = 0.0
        self.peak_growth_rate = 0.0

    def update(self, current_time: datetime, day_high: float):
        """
        更新 Day High 記錄

        Args:
            current_time: 當前時間
            day_high: 當前 Day High
        """
        # 添加新記錄
        self.day_high_history.append({
            'time': current_time,
            'day_high': day_high
        })

        # 更新當前 day_high
        self.current_day_high = day_high

        # 清理過期記錄（超過 window_seconds 的）
        cutoff_time = current_time - timedelta(seconds=self.window_seconds)
        while self.day_high_history and self.day_high_history[0]['time'] < cutoff_time:
            self.day_high_history.popleft()

        # 計算一分鐘前的 day_high
        if self.day_high_history:
            self.day_high_1min_ago = self.day_high_history[0]['day_high']
        else:
            self.day_high_1min_ago = day_high

        # 計算並更新成長率序列
        self.get_growth_rate()

    def get_growth_rate(self) -> float:
        """
        計算 Day High 增長率

        Returns:
            增長率（比例值）
        """
        if self.day_high_1min_ago > 0 and self.current_day_high != self.day_high_1min_ago:
            self.last_growth_rate = (self.current_day_high - self.day_high_1min_ago) / self.day_high_1min_ago
        else:
            self.last_growth_rate = 0.0

        self.growth_rate_history.append(self.last_growth_rate)
        self.peak_growth_rate = max(self.peak_growth_rate, self.last_growth_rate)
        return self.last_growth_rate

    def is_growth_rate_turning_down(self) -> bool:
        """
        判斷 Day High 增長率是否從上升轉為下降

        Returns:
            是否轉為下降
        """
        if len(self.growth_rate_history) < 2:
            return False
        prev_rate = self.growth_rate_history[-2]
        curr_rate = self.growth_rate_history[-1]
        return curr_rate < prev_rate

    def get_growth_drawdown(self) -> float:
        """
        計算增長率從峰值的回落幅度

        Returns:
            回落幅度（比例值）
        """
        return max(self.peak_growth_rate - self.last_growth_rate, 0.0)


class OrderBookBalanceMonitor:
    """掛單結構平衡監控器"""

    def __init__(self, thin_threshold: int = 20, normal_threshold: int = 40):
        """
        初始化掛單監控器

        Args:
            thin_threshold: 極薄掛單門檻
            normal_threshold: 正常掛單門檻
        """
        self.thin_threshold = thin_threshold
        self.normal_threshold = normal_threshold

    def calculate_bid_thickness(self, row) -> float:
        """
        計算買方掛單厚度（使用個別五檔加總）

        Args:
            row: 資料列

        Returns:
            買方掛單厚度
        """
        # 先嘗試使用 bid_volume_5level
        bid_total = row.get('bid_volume_5level', 0)

        # 如果 bid_volume_5level 為 0 或不存在，使用個別檔位加總
        if pd.isna(bid_total) or bid_total == 0:
            bid_volumes = []
            for i in range(1, 6):
                vol = row.get(f'bid{i}_volume', 0)
                if pd.notna(vol):
                    bid_volumes.append(vol)

            if bid_volumes:
                bid_total = sum(bid_volumes)

        # 除以5取得平均值作為厚度指標
        if bid_total > 0:
            return bid_total / 5
        return 0.0

    def calculate_ask_thickness(self, row) -> float:
        """
        計算賣方掛單厚度（使用個別五檔加總）

        Args:
            row: 資料列

        Returns:
            賣方掛單厚度
        """
        # 先嘗試使用 ask_volume_5level
        ask_total = row.get('ask_volume_5level', 0)

        # 如果 ask_volume_5level 為 0 或不存在，使用個別檔位加總
        if pd.isna(ask_total) or ask_total == 0:
            ask_volumes = []
            for i in range(1, 6):
                vol = row.get(f'ask{i}_volume', 0)
                if pd.notna(vol):
                    ask_volumes.append(vol)

            if ask_volumes:
                ask_total = sum(ask_volumes)

        # 除以5取得平均值作為厚度指標
        if ask_total > 0:
            return ask_total / 5
        return 0.0

    def calculate_balance_ratio(self, row) -> float:
        """
        計算買賣掛單平衡度

        Args:
            row: 資料列

        Returns:
            買賣平衡比率（買量/賣量）
        """
        # 計算總買量
        total_bid = row.get('bid_volume_5level', 0)
        if pd.isna(total_bid) or total_bid == 0:
            bid_volumes = []
            for i in range(1, 6):
                vol = row.get(f'bid{i}_volume', 0)
                if pd.notna(vol):
                    bid_volumes.append(vol)
            total_bid = sum(bid_volumes) if bid_volumes else 0

        # 計算總賣量
        total_ask = row.get('ask_volume_5level', 0)
        if pd.isna(total_ask) or total_ask == 0:
            ask_volumes = []
            for i in range(1, 6):
                vol = row.get(f'ask{i}_volume', 0)
                if pd.notna(vol):
                    ask_volumes.append(vol)
            total_ask = sum(ask_volumes) if ask_volumes else 0

        if total_bid > 0 and total_ask > 0:
            return total_bid / total_ask
        return 0.0

    def is_order_book_recovering(self, current_row, entry_bid_thickness: float) -> bool:
        """
        判斷掛單是否從薄變厚

        Args:
            current_row: 當前資料列
            entry_bid_thickness: 進場時的買方掛單厚度

        Returns:
            是否恢復
        """
        current_bid_thickness = self.calculate_bid_thickness(current_row)

        # 進場時是薄掛單，現在變成正常掛單
        return (entry_bid_thickness < self.thin_threshold and
                current_bid_thickness > self.normal_threshold)


class OutsideVolumeTracker:
    """
    外盤成交金額追蹤器
    用於追蹤指定時間窗口內的外盤成交金額
    """

    def __init__(self, window_seconds: int = 3):
        """
        初始化外盤金額追蹤器

        Args:
            window_seconds: 追蹤窗口時間（秒），預設3秒
        """
        self.window_seconds = window_seconds
        self.trades_window = deque()  # 保存指定窗口內的交易記錄
        self.total_volume = 0.0  # 當前窗口內的總金額

    def update_trades(
        self,
        current_time: datetime,
        tick_type: int,
        price: float,
        volume: float
    ):
        """
        更新外盤交易記錄

        Args:
            current_time: 當前時間
            tick_type: Tick 類型（1=外盤, 2=內盤）
            price: 成交價
            volume: 成交量（張）
        """
        # 清理過期記錄
        cutoff_time = current_time - timedelta(seconds=self.window_seconds)
        while self.trades_window and self.trades_window[0]['time'] < cutoff_time:
            expired_trade = self.trades_window.popleft()
            self.total_volume -= expired_trade['amount']

        # 只記錄外盤交易（tick_type == 1）
        if tick_type == 1:
            trade_amount = price * volume * 1000  # 轉換為金額（張數 * 1000）
            self.trades_window.append({
                'time': current_time,
                'amount': trade_amount
            })
            self.total_volume += trade_amount

    def get_volume_3s(self) -> float:
        """
        取得過去3秒的外盤成交金額

        Returns:
            過去3秒的外盤成交金額
        """
        return self.total_volume

    def get_current_volume(self) -> float:
        """
        取得當前窗口內的外盤成交金額（同 get_volume_3s）

        Returns:
            當前窗口內的外盤成交金額
        """
        return self.total_volume

    def compare_with_entry(self, entry_volume: float) -> Tuple[float, bool]:
        """
        比較當前外盤金額與進場時的外盤金額

        Args:
            entry_volume: 進場時的外盤金額

        Returns:
            (當前金額, 是否大於進場金額)
        """
        current = self.total_volume
        is_greater = current > entry_volume
        return current, is_greater

    def reset(self):
        """重置追蹤器"""
        self.trades_window.clear()
        self.total_volume = 0.0

    def get_trade_count(self) -> int:
        """
        取得當前窗口內的交易筆數

        Returns:
            交易筆數
        """
        return len(self.trades_window)

    def get_average_trade_amount(self) -> float:
        """
        計算平均每筆交易金額

        Returns:
            平均交易金額，若無交易則返回0
        """
        if not self.trades_window:
            return 0.0
        return self.total_volume / len(self.trades_window)


class MassiveMatchingTracker:
    """大量搓合追蹤器"""

    def __init__(self, window_seconds: int = 1):
        """
        初始化大量搓合追蹤器

        Args:
            window_seconds: 追蹤窗口時間（秒），預設1秒
        """
        self.window_seconds = window_seconds
        self.outside_tracker = OutsideVolumeTracker(window_seconds=window_seconds)

    def update(
        self,
        current_time: datetime,
        tick_type: int,
        price: float,
        volume: float
    ):
        """
        更新大量搓合記錄

        Args:
            current_time: 當前時間
            tick_type: Tick 類型
            price: 成交價
            volume: 成交量
        """
        self.outside_tracker.update_trades(current_time, tick_type, price, volume)

    def get_massive_matching_amount(self) -> float:
        """
        取得大量搓合金額（指定時間窗口內的外盤成交金額）

        Returns:
            大量搓合金額
        """
        return self.outside_tracker.get_current_volume()

    def check_threshold(self, threshold: float) -> bool:
        """
        檢查是否達到大量搓合門檻

        Args:
            threshold: 門檻金額

        Returns:
            是否達到門檻
        """
        return self.get_massive_matching_amount() >= threshold


class InsideOutsideRatioTracker:
    """
    內外盤比追蹤器
    用於追蹤指定時間窗口內的內外盤成交量比率
    """

    def __init__(self, window_seconds: int = 60, min_volume_threshold: float = 0):
        """
        初始化內外盤比追蹤器

        Args:
            window_seconds: 追蹤窗口時間（秒），預設60秒
            min_volume_threshold: 最小成交量門檻，只追蹤大於此門檻的交易，預設0（追蹤所有交易）
        """
        self.window_seconds = window_seconds
        self.min_volume_threshold = min_volume_threshold
        self.inside_trades = deque()   # 內盤交易記錄
        self.outside_trades = deque()  # 外盤交易記錄
        self.inside_volume = 0.0       # 內盤累積成交量
        self.outside_volume = 0.0      # 外盤累積成交量

    def update(
        self,
        current_time: datetime,
        tick_type: int,
        price: float,
        volume: float
    ):
        """
        更新內外盤交易記錄

        Args:
            current_time: 當前時間
            tick_type: Tick 類型（1=外盤, 2=內盤）
            price: 成交價
            volume: 成交量（張）
        """
        # 檢查是否達到最小成交量門檻
        if volume <= self.min_volume_threshold:
            # 只清理過期記錄，但不新增
            self._cleanup_expired_trades(current_time)
            return

        # 清理過期記錄
        self._cleanup_expired_trades(current_time)

        # 新增交易記錄
        trade_volume = volume  # 保持成交量（張數）

        if tick_type == 1:  # 外盤
            self.outside_trades.append({
                'time': current_time,
                'volume': trade_volume
            })
            self.outside_volume += trade_volume

        elif tick_type == 2:  # 內盤
            self.inside_trades.append({
                'time': current_time,
                'volume': trade_volume
            })
            self.inside_volume += trade_volume

    def _cleanup_expired_trades(self, current_time: datetime):
        """清理過期的交易記錄"""
        cutoff_time = current_time - timedelta(seconds=self.window_seconds)

        # 清理內盤過期記錄
        while self.inside_trades and self.inside_trades[0]['time'] < cutoff_time:
            expired_trade = self.inside_trades.popleft()
            self.inside_volume -= expired_trade['volume']

        # 清理外盤過期記錄
        while self.outside_trades and self.outside_trades[0]['time'] < cutoff_time:
            expired_trade = self.outside_trades.popleft()
            self.outside_volume -= expired_trade['volume']

    def get_ratio(self) -> float:
        """
        計算內外盤比（內盤量/外盤量）

        Returns:
            內外盤比，如果外盤量為0則返回0
        """
        if self.outside_volume > 0:
            return self.inside_volume / self.outside_volume
        return 0.0

    def get_outside_ratio(self) -> float:
        """
        計算外盤佔比（外盤量/(內盤量+外盤量)）

        Returns:
            外盤佔比，如果總量為0則返回0.5
        """
        total = self.inside_volume + self.outside_volume
        if total > 0:
            return self.outside_volume / total
        return 0.5

    def get_volumes(self) -> Tuple[float, float]:
        """
        取得內外盤成交量

        Returns:
            (內盤量, 外盤量)
        """
        return self.inside_volume, self.outside_volume

    def reset(self):
        """重置追蹤器"""
        self.inside_trades.clear()
        self.outside_trades.clear()
        self.inside_volume = 0.0
        self.outside_volume = 0.0