"""
小單推升過濾模組
用於檢測和過濾小單堆疊造成的假突破
新增突破品質檢查功能
"""

import pandas as pd
import numpy as np
import logging
from typing import Tuple, Optional, Dict

logger = logging.getLogger(__name__)


class SmallOrderFilter:
    """小單推升過濾器與突破品質檢查器"""

    def __init__(self, config):
        """
        初始化小單過濾器

        Args:
            config: 策略設定物件
        """
        # 小單過濾開關（舊策略）
        self.enabled = config.get('small_order_filter_enabled', False)  # 預設關閉

        # 突破品質檢查（新策略）
        self.breakout_quality_enabled = config.get('breakout_quality_check_enabled', True)
        self.breakout_min_volume = config.get('breakout_min_volume', 10)  # 最小成交量
        self.breakout_min_ask_eat_ratio = config.get('breakout_min_ask_eat_ratio', 0.25)  # 吃檔比例
        self.breakout_absolute_large_volume = config.get('breakout_absolute_large_volume', 50)  # 絕對大單

        # 檢查範圍
        self.check_trades = config.get('small_order_check_trades', 30)

        # 小單定義門檻
        self.small_threshold = config.get('small_order_threshold', 3)      # ≤3張為小單
        self.tiny_threshold = config.get('tiny_order_threshold', 2)        # ≤2張為極小單
        self.single_threshold = config.get('single_order_threshold', 1)    # 1張單

        # 過濾門檻
        self.small_ratio_limit = config.get('small_order_ratio_limit', 0.8)      # 80%
        self.tiny_ratio_limit = config.get('tiny_order_ratio_limit', 0.5)        # 50%
        self.single_ratio_limit = config.get('single_order_ratio_limit', 0.4)    # 40%

        # 大單確認
        self.require_large_order = config.get('require_large_order_confirmation', False)
        self.large_threshold = config.get('large_order_threshold', 20)
        self.min_large_orders = config.get('min_large_orders', 2)

        if self.enabled:
            logger.info(f"小單過濾器已初始化: 啟用={self.enabled}, 檢查{self.check_trades}筆交易")
        if self.breakout_quality_enabled:
            logger.info(f"突破品質檢查已啟用: 最小量={self.breakout_min_volume}張, 吃檔比例={self.breakout_min_ask_eat_ratio:.1%}")

    def check_small_order_pattern(
        self,
        df: pd.DataFrame,
        current_time: pd.Timestamp
    ) -> Tuple[bool, str]:
        """
        檢查是否為小單推升模式

        Args:
            df: 包含成交資料的 DataFrame
            current_time: 當前時間

        Returns:
            (是否通過檢查, 原因說明)
        """
        if not self.enabled:
            return True, "小單過濾已停用"

        # 只取交易資料（type == 'Trade'）
        trade_df = df[df['type'] == 'Trade'].copy()

        if len(trade_df) == 0:
            return True, "無交易資料"

        # 取得最近 N 筆交易
        recent_trades = trade_df.tail(self.check_trades)

        if len(recent_trades) < 10:  # 至少要有10筆交易才判斷
            return True, f"交易筆數不足 ({len(recent_trades)}筆)"

        # 計算小單比例
        single_orders = recent_trades[recent_trades['volume'] == self.single_threshold]
        tiny_orders = recent_trades[recent_trades['volume'] <= self.tiny_threshold]
        small_orders = recent_trades[recent_trades['volume'] <= self.small_threshold]

        single_ratio = len(single_orders) / len(recent_trades)
        tiny_ratio = len(tiny_orders) / len(recent_trades)
        small_ratio = len(small_orders) / len(recent_trades)

        # 檢查是否超過門檻
        if single_ratio > self.single_ratio_limit:
            logger.info(f"  ❌ 小單過濾: 1張單比例過高 {single_ratio:.1%} > {self.single_ratio_limit:.1%}")
            return False, f"1張單比例過高: {single_ratio:.1%}"

        if tiny_ratio > self.tiny_ratio_limit:
            logger.info(f"  ❌ 小單過濾: 極小單比例過高 {tiny_ratio:.1%} > {self.tiny_ratio_limit:.1%}")
            return False, f"極小單(≤{self.tiny_threshold}張)比例過高: {tiny_ratio:.1%}"

        if small_ratio > self.small_ratio_limit:
            logger.info(f"  ❌ 小單過濾: 小單比例過高 {small_ratio:.1%} > {self.small_ratio_limit:.1%}")
            return False, f"小單(≤{self.small_threshold}張)比例過高: {small_ratio:.1%}"

        # 大單確認（可選）
        if self.require_large_order:
            large_orders = recent_trades[recent_trades['volume'] >= self.large_threshold]
            if len(large_orders) < self.min_large_orders:
                logger.info(f"  ❌ 小單過濾: 缺乏大單確認 (只有{len(large_orders)}筆≥{self.large_threshold}張)")
                return False, f"缺乏大單確認 (需要{self.min_large_orders}筆≥{self.large_threshold}張)"

        # 記錄通過資訊
        logger.debug(f"  ✓ 小單過濾通過: 1張={single_ratio:.1%}, ≤2張={tiny_ratio:.1%}, ≤3張={small_ratio:.1%}")

        return True, "通過小單檢測"

    def get_trade_statistics(self, df: pd.DataFrame, current_time: pd.Timestamp) -> dict:
        """
        取得交易統計資訊（用於除錯和分析）

        Args:
            df: 包含成交資料的 DataFrame
            current_time: 當前時間

        Returns:
            包含統計資訊的字典
        """
        # 只取交易資料
        trade_df = df[df['type'] == 'Trade'].copy()

        if len(trade_df) == 0:
            return {}

        # 取得最近 N 筆交易
        recent_trades = trade_df.tail(self.check_trades)

        if len(recent_trades) == 0:
            return {}

        # 計算各種統計
        stats = {
            'total_trades': len(recent_trades),
            'total_volume': recent_trades['volume'].sum(),
            'avg_volume': recent_trades['volume'].mean(),
            'median_volume': recent_trades['volume'].median(),
            'single_count': len(recent_trades[recent_trades['volume'] == 1]),
            'tiny_count': len(recent_trades[recent_trades['volume'] <= self.tiny_threshold]),
            'small_count': len(recent_trades[recent_trades['volume'] <= self.small_threshold]),
            'large_count': len(recent_trades[recent_trades['volume'] >= self.large_threshold]),
        }

        # 計算比例
        if stats['total_trades'] > 0:
            stats['single_ratio'] = stats['single_count'] / stats['total_trades']
            stats['tiny_ratio'] = stats['tiny_count'] / stats['total_trades']
            stats['small_ratio'] = stats['small_count'] / stats['total_trades']
            stats['large_ratio'] = stats['large_count'] / stats['total_trades']

        return stats

    def format_statistics(self, stats: dict) -> str:
        """
        格式化統計資訊為字串

        Args:
            stats: 統計資訊字典

        Returns:
            格式化的字串
        """
        if not stats:
            return "無統計資料"

        lines = [
            f"最近{stats.get('total_trades', 0)}筆交易統計:",
            f"  總成交量: {stats.get('total_volume', 0):.0f}張",
            f"  平均: {stats.get('avg_volume', 0):.1f}張, 中位數: {stats.get('median_volume', 0):.0f}張",
            f"  1張單: {stats.get('single_count', 0)}筆 ({stats.get('single_ratio', 0):.1%})",
            f"  ≤{self.tiny_threshold}張: {stats.get('tiny_count', 0)}筆 ({stats.get('tiny_ratio', 0):.1%})",
            f"  ≤{self.small_threshold}張: {stats.get('small_count', 0)}筆 ({stats.get('small_ratio', 0):.1%})",
            f"  ≥{self.large_threshold}張: {stats.get('large_count', 0)}筆 ({stats.get('large_ratio', 0):.1%})",
        ]

        return '\n'.join(lines)

    def check_breakout_quality(
        self,
        current_row: pd.Series,
        is_day_high_breakout: bool = False,
        df: pd.DataFrame = None
    ) -> Tuple[bool, str]:
        """
        檢查Day High突破的品質
        新邏輯：偵測突破前是否有大單吃掉 ask 五檔，我們是跟隨大單進場

        Args:
            current_row: 當前的交易資料（包含成交量和五檔資訊）
            is_day_high_breakout: 是否為Day High突破
            df: 完整的資料 DataFrame（用於查看歷史交易）

        Returns:
            (是否通過檢查, 原因說明)
        """
        if not self.breakout_quality_enabled:
            return True, "突破品質檢查已停用"

        if not is_day_high_breakout:
            return True, "非Day High突破"

        # 如果沒有提供 df，使用舊邏輯（向後相容）
        if df is None:
            # 舊邏輯：檢查突破當筆
            breakout_volume = current_row.get('volume', 0)
            if breakout_volume >= self.breakout_absolute_large_volume:
                return True, f"絕對大單突破 ({breakout_volume:.0f}張)"
            elif breakout_volume >= self.breakout_min_volume:
                return True, f"突破量足夠 ({breakout_volume:.0f}張)"
            else:
                return False, f"突破量過小: {breakout_volume:.0f}張"

        # 新邏輯：檢查當前突破的這筆交易是否為大單清掃 ask 五檔
        # 我們只看「當前這一筆」或「前一筆」（造成突破的那筆）
        current_time = current_row.get('time')
        if pd.isna(current_time):
            return True, "無法取得時間資訊"

        # 取得當前交易的成交量
        current_volume = current_row.get('volume', 0)

        # 計算 ask 五檔總量（使用前一筆的五檔資訊，因為當前筆已經成交了）
        ask_total = 0

        # 如果有提供 df，嘗試取得前一筆的五檔資訊
        if df is not None:
            trade_df = df[df['type'] == 'Trade'].copy()
            # 找到當前時間之前最近的一筆
            prev_trades = trade_df[trade_df['time'] < current_time]
            if not prev_trades.empty:
                prev_row = prev_trades.iloc[-1]
                ask_total = prev_row.get('ask_volume_5level', 0)
                if pd.isna(ask_total) or ask_total == 0:
                    # 手動計算五檔總和
                    ask_volumes = []
                    for i in range(1, 6):
                        vol = prev_row.get(f'ask{i}_volume', 0)
                        if pd.notna(vol):
                            ask_volumes.append(vol)
                    ask_total = sum(ask_volumes) if ask_volumes else 0

        # 如果無法取得前一筆的五檔，使用當前筆的五檔（可能已更新）
        if ask_total == 0:
            ask_total = current_row.get('ask_volume_5level', 0)
            if pd.isna(ask_total) or ask_total == 0:
                ask_volumes = []
                for i in range(1, 6):
                    vol = current_row.get(f'ask{i}_volume', 0)
                    if pd.notna(vol):
                        ask_volumes.append(vol)
                ask_total = sum(ask_volumes) if ask_volumes else 0

        # 條件1: 絕對大單（>= 50張）
        if current_volume >= self.breakout_absolute_large_volume:
            logger.debug(f"  ✓ 突破品質: 當前為絕對大單 {current_volume:.0f}張")
            return True, f"絕對大單突破 ({current_volume:.0f}張)"

        # 條件2: 一般大單（>= 10張）且吃掉 ask 五檔的 25% 以上
        if current_volume >= self.breakout_min_volume:
            if ask_total > 0:
                eat_ratio = current_volume / ask_total
                if eat_ratio >= self.breakout_min_ask_eat_ratio:
                    logger.debug(f"  ✓ 突破品質: 大單 {current_volume:.0f}張 吃 ask五檔 {eat_ratio:.0%}")
                    return True, f"大單吃掉五檔 {eat_ratio:.0%} ({current_volume:.0f}張/{ask_total:.0f}張)"
                else:
                    logger.info(f"  ❌ 突破品質: 吃五檔比例不足 {eat_ratio:.0%} < {self.breakout_min_ask_eat_ratio:.0%}")
                    return False, f"吃五檔不足 {eat_ratio:.0%}"
            else:
                # 如果 ask 五檔為 0，只要有大單就算
                logger.debug(f"  ✓ 突破品質: 大單 {current_volume:.0f}張 (五檔為空)")
                return True, f"大單突破 ({current_volume:.0f}張)"

        # 成交量太小
        logger.info(f"  ❌ 突破品質: 成交量過小 {current_volume:.0f}張 < {self.breakout_min_volume}張")
        return False, f"突破量過小: {current_volume:.0f}張"

    def analyze_breakout_context(
        self,
        df: pd.DataFrame,
        breakout_time: pd.Timestamp,
        seconds_before: int = 3,
        seconds_after: int = 3
    ) -> Dict:
        """
        分析突破前後的成交特徵對比

        Args:
            df: 包含成交資料的 DataFrame
            breakout_time: 突破時間
            seconds_before: 分析突破前幾秒
            seconds_after: 分析突破後幾秒

        Returns:
            包含分析結果的字典
        """
        # 只取交易資料
        trade_df = df[df['type'] == 'Trade'].copy()

        # 突破前
        before_start = breakout_time - pd.Timedelta(seconds=seconds_before)
        before_mask = (trade_df['time'] >= before_start) & (trade_df['time'] < breakout_time)
        before_trades = trade_df[before_mask]

        # 突破後
        after_end = breakout_time + pd.Timedelta(seconds=seconds_after)
        after_mask = (trade_df['time'] > breakout_time) & (trade_df['time'] <= after_end)
        after_trades = trade_df[after_mask]

        # 計算特徵
        before_avg_volume = before_trades['volume'].mean() if len(before_trades) > 0 else 0
        after_avg_volume = after_trades['volume'].mean() if len(after_trades) > 0 else 0
        before_total_volume = before_trades['volume'].sum() if len(before_trades) > 0 else 0
        after_total_volume = after_trades['volume'].sum() if len(after_trades) > 0 else 0

        # 成交量放大倍數
        volume_amplification = after_avg_volume / before_avg_volume if before_avg_volume > 0 else 1

        return {
            'before_avg_volume': before_avg_volume,
            'after_avg_volume': after_avg_volume,
            'before_total_volume': before_total_volume,
            'after_total_volume': after_total_volume,
            'volume_amplification': volume_amplification,
            'before_trades_count': len(before_trades),
            'after_trades_count': len(after_trades)
        }