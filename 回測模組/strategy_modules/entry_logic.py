"""
進場邏輯模組
負責檢查Day High突破進場條件，並記錄詳細的進場訊號
"""

import logging
import pandas as pd
from datetime import datetime
from typing import Dict, Optional, List
from dataclasses import dataclass, field
from .small_order_filter import SmallOrderFilter

logger = logging.getLogger(__name__)


@dataclass
class EntrySignal:
    """進場訊號記錄"""
    time: datetime
    stock_id: str
    price: float
    day_high: float
    passed: bool
    conditions: Dict[str, Dict] = field(default_factory=dict)
    failure_reason: str = ""

    def to_dict(self) -> Dict:
        """轉換為字典格式"""
        return {
            'time': self.time.strftime('%H:%M:%S.%f')[:12],
            'stock_id': self.stock_id,
            'price': self.price,
            'day_high': self.day_high,
            'passed': self.passed,
            'conditions': self.conditions,
            'failure_reason': self.failure_reason
        }


class EntryChecker:
    """進場條件檢查器"""

    def __init__(self, config: Dict):
        """
        初始化進場檢查器

        Args:
            config: 進場設定字典
        """
        self.config = config
        self.entry_signals: List[EntrySignal] = []  # 記錄所有進場訊號

        # 初始化小單過濾器
        self.small_order_filter = SmallOrderFilter(config)

        # 設定進場條件權重（用於顯示順序）
        self.condition_order = [
            'Day High突破',
            '時間檢查',
            '冷卻期檢查',
            '漲停檢查',
            '平盤檢查',
            '漲幅上限',
            '五檔條件',
            '大量搓合',
            '外盤金額比較',
            'Ratio條件',
            '區間漲幅',
            '10分鐘低點',
            '小單過濾',
            '非漲停價'
        ]

    def check_day_high_breakout(self, current_day_high: float, prev_day_high: float) -> bool:
        """
        檢查是否有 Day High 突破

        Args:
            current_day_high: 當前 Day High
            prev_day_high: 前一個 Day High

        Returns:
            是否突破
        """
        return current_day_high > prev_day_high and prev_day_high > 0

    def check_basic_conditions(
        self,
        stock_id: str,
        current_time: datetime,
        current_price: float,
        ref_price: float,
        limit_up_price: float,
        last_entry_time: Optional[datetime] = None
    ) -> Dict:
        """
        檢查基本進場條件

        Args:
            stock_id: 股票代碼
            current_time: 當前時間
            current_price: 當前價格
            ref_price: 參考價格（昨收）
            limit_up_price: 漲停價
            last_entry_time: 上次進場時間

        Returns:
            檢查結果字典
        """
        result = {'pass': True, 'conditions': {}, 'failure_reason': ''}

        # 1. 時間檢查（必須 >= entry_start_time）
        if current_time.time() < self.config['entry_start_time']:
            result['conditions']['時間檢查'] = {
                'pass': False,
                'value': current_time.strftime('%H:%M:%S'),
                'threshold': self.config['entry_start_time'].strftime('%H:%M:%S'),
                'message': '未到進場時間'
            }
            result['pass'] = False
            result['failure_reason'] = '未到進場時間'
            return result

        result['conditions']['時間檢查'] = {
            'pass': True,
            'value': current_time.strftime('%H:%M:%S'),
            'threshold': self.config['entry_start_time'].strftime('%H:%M:%S'),
            'message': '已到進場時間'
        }

        # 2. 冷卻期檢查
        if last_entry_time is not None:
            time_since_last = (current_time - last_entry_time).total_seconds()
            if time_since_last < self.config['entry_cooldown']:
                result['conditions']['冷卻期檢查'] = {
                    'pass': False,
                    'value': f"{time_since_last:.1f}秒",
                    'threshold': f"{self.config['entry_cooldown']}秒",
                    'message': '仍在冷卻期'
                }
                result['pass'] = False
                result['failure_reason'] = '仍在冷卻期'
                return result

        result['conditions']['冷卻期檢查'] = {
            'pass': True,
            'value': '無冷卻限制',
            'message': '已過冷卻期或首次進場'
        }

        # 3. 非漲停檢查
        if current_price >= limit_up_price:
            result['conditions']['漲停檢查'] = {
                'pass': False,
                'value': f"{current_price:.2f}",
                'threshold': f"{limit_up_price:.2f}",
                'message': '已達漲停價'
            }
            result['pass'] = False
            result['failure_reason'] = '已達漲停價'
            return result

        result['conditions']['漲停檢查'] = {
            'pass': True,
            'value': f"{current_price:.2f}",
            'threshold': f"{limit_up_price:.2f}",
            'message': '未達漲停價'
        }

        # 4. 進場截止時間檢查
        if current_time.time() >= self.config['entry_cutoff_time']:
            result['conditions']['截止時間'] = {
                'pass': False,
                'value': current_time.strftime('%H:%M:%S'),
                'threshold': self.config['entry_cutoff_time'].strftime('%H:%M:%S'),
                'message': '已過進場截止時間'
            }
            result['pass'] = False
            result['failure_reason'] = '已過進場截止時間'
            return result

        return result

    def check_entry_signals(
        self,
        stock_id: str,
        current_price: float,
        current_time: datetime,
        prev_day_high: float,
        indicators: Dict,
        ask_bid_ratio: float,
        ref_price: float,
        massive_matching_amount: float = 0.0,
        fixed_ratio_threshold: Optional[float] = None,
        dynamic_ratio_threshold: Optional[float] = None,
        min_outside_amount: Optional[float] = None,
        force_log: bool = False,
        df: Optional[pd.DataFrame] = None,
        current_row: Optional[pd.Series] = None,
        is_day_high_breakout: bool = False,
        last_exit_time: Optional[datetime] = None  # 新增參數
    ) -> EntrySignal:
        """
        檢查所有進場條件（參考 run_backtest_feature.py 的 check_entry 函數）

        Args:
            stock_id: 股票代碼
            current_price: 當前價格
            current_time: 當前時間
            prev_day_high: 前一個 Day High
            indicators: 技術指標字典
            ask_bid_ratio: 委賣/委買比率
            ref_price: 參考價格（昨收）
            massive_matching_amount: 大量搓合金額
            fixed_ratio_threshold: 固定Ratio門檻
            dynamic_ratio_threshold: 動態Ratio門檻
            min_outside_amount: 最小外盤金額
            force_log: 是否強制記錄日誌

        Returns:
            EntrySignal 物件
        """
        # 建立訊號記錄
        signal = EntrySignal(
            time=current_time,
            stock_id=stock_id,
            price=current_price,
            day_high=prev_day_high,
            passed=False
        )

        # 記錄所有條件檢查結果
        should_log = force_log or logger.isEnabledFor(logging.DEBUG)

        if should_log:
            logger.info(f"")
            logger.info(f"{'='*60}")
            logger.info(f"進場條件檢查 | {stock_id} | {current_time.strftime('%H:%M:%S.%f')[:12]}")
            logger.info(f"{'='*60}")
            logger.info(f"當前價格: {current_price:.2f} | Day High: {prev_day_high:.2f} | 昨收: {ref_price:.2f}")

        # 0. 時間檢查（必須在 entry_start_time 和 entry_end_time 之間）
        entry_start_time = self.config.get('entry_start_time')
        entry_end_time = self.config.get('entry_end_time')  # 新增結束時間檢查

        # 檢查是否已過進場時間窗口（早盤策略專用）
        if self.config.get('entry_mode') in ['early_0901', 'early_0905'] and entry_end_time:
            if current_time.time() > entry_end_time:
                signal.conditions['時間檢查'] = {
                    'pass': False,
                    'value': current_time.strftime('%H:%M:%S'),
                    'threshold': f"{entry_start_time.strftime('%H:%M:%S')}-{entry_end_time.strftime('%H:%M:%S')}",
                    'message': '已過進場時間窗口'
                }
                if should_log:
                    logger.info(
                        f"  [0] ❌ 時間檢查: 失敗 | {current_time.strftime('%H:%M:%S')} > "
                        f"{entry_end_time.strftime('%H:%M:%S')} (進場窗口已結束)"
                    )
                signal.failure_reason = "已過進場時間窗口"
                self.entry_signals.append(signal)
                return signal

        # 檢查是否已達進場開始時間
        if entry_start_time and current_time.time() < entry_start_time:
            signal.conditions['時間檢查'] = {
                'pass': False,
                'value': current_time.strftime('%H:%M:%S'),
                'threshold': entry_start_time.strftime('%H:%M:%S'),
                'message': '未到進場時間'
            }
            if should_log:
                logger.info(
                    f"  [0] ❌ 時間檢查: 失敗 | {current_time.strftime('%H:%M:%S')} < "
                    f"{entry_start_time.strftime('%H:%M:%S')}"
                )
            signal.failure_reason = "未到進場時間"
            self.entry_signals.append(signal)
            return signal
        elif entry_start_time:
            time_window_msg = f"{entry_start_time.strftime('%H:%M:%S')}"
            if entry_end_time and self.config.get('entry_mode') in ['early_0901', 'early_0905']:
                time_window_msg += f"-{entry_end_time.strftime('%H:%M:%S')}"

            signal.conditions['時間檢查'] = {
                'pass': True,
                'value': current_time.strftime('%H:%M:%S'),
                'threshold': time_window_msg,
                'message': '在進場時間窗口內'
            }
            if should_log:
                logger.info(
                    f"  [0] ✓ 時間檢查: 通過 | {current_time.strftime('%H:%M:%S')} 在 "
                    f"{time_window_msg} 窗口內"
                )

        # 0.5 冷卻期檢查（出場後30秒內不能再進場）
        if last_exit_time is not None:
            time_since_exit = (current_time - last_exit_time).total_seconds()
            cooldown_period = self.config.get('entry_cooldown', 30.0)
            signal.conditions['冷卻期檢查'] = {
                'pass': time_since_exit >= cooldown_period,
                'value': f"{time_since_exit:.1f}秒",
                'threshold': f"{cooldown_period}秒",
                'message': '已過出場冷卻期' if time_since_exit >= cooldown_period else '仍在出場冷卻期'
            }

            if time_since_exit < cooldown_period:
                if should_log:
                    logger.info(
                        f"  [0.5] ❌ 冷卻期檢查: 失敗 | 距離出場 {time_since_exit:.1f}秒 < {cooldown_period}秒"
                    )
                signal.failure_reason = "出場冷卻期內"
                self.entry_signals.append(signal)
                return signal
            elif should_log:
                logger.info(
                    f"  [0.5] ✓ 冷卻期檢查: 通過 | 距離出場 {time_since_exit:.1f}秒 ≥ {cooldown_period}秒"
                )
        else:
            signal.conditions['冷卻期檢查'] = {
                'pass': True,
                'value': '無出場記錄',
                'message': '首次進場或無前次出場記錄'
            }
            if should_log:
                logger.info(f"  [0.5] ✓ 冷卻期檢查: 通過 | 無前次出場記錄")

        # 1. 價格 > 平盤 (可選條件)
        price_change_pct = (current_price - ref_price) / ref_price * 100 if ref_price > 0 else 0.0

        # 檢查是否啟用平盤檢查
        above_open_check_enabled = self.config.get('above_open_check_enabled', True)  # 預設為 True 以保持向後相容

        if above_open_check_enabled:
            signal.conditions['平盤檢查'] = {
                'pass': current_price > ref_price,
                'value': f"{current_price:.2f} vs {ref_price:.2f}",
                'pct_change': f"{price_change_pct:.2f}%"
            }

            if current_price <= ref_price:
                if should_log:
                    logger.info(f"  [1] ❌ 平盤檢查: 失敗 | 現價 {current_price:.2f} ≤ 昨收 {ref_price:.2f}")
                signal.failure_reason = "低於平盤"
                self.entry_signals.append(signal)
                return signal
            elif should_log:
                logger.info(f"  [1] ✓ 平盤檢查: 通過 | 漲幅 {price_change_pct:.2f}%")
        else:
            # 平盤檢查被禁用，直接通過
            signal.conditions['平盤檢查'] = {
                'pass': True,
                'value': '檢查已禁用',
                'pct_change': f"{price_change_pct:.2f}%"
            }
            if should_log:
                logger.info(f"  [1] ⊙ 平盤檢查: 已禁用 | 漲幅 {price_change_pct:.2f}%")

        # 2. 漲幅上限檢查（*** 改為 8.5% ***）
        price_limit = self.config.get('price_change_limit_pct', 8.5)
        signal.conditions['漲幅上限'] = {
            'pass': price_change_pct <= price_limit,
            'value': f"{price_change_pct:.2f}%",
            'threshold': f"{price_limit}%"
        }

        if self.config.get('price_change_limit_enabled', True) and price_change_pct > price_limit:
            if should_log:
                logger.info(f"  [2] ❌ 漲幅檢查: 失敗 | {price_change_pct:.2f}% > {price_limit}%")
            signal.failure_reason = f"漲幅超過{price_limit}%"
            self.entry_signals.append(signal)
            return signal
        elif should_log:
            logger.info(f"  [2] ✓ 漲幅檢查: 通過 | {price_change_pct:.2f}% ≤ {price_limit}%")

        # 3. 五檔條件（委賣總量 / 委買總量 > threshold）
        ask_bid_threshold = self.config.get('ask_bid_ratio_threshold', 1.0)
        signal.conditions['五檔條件'] = {
            'pass': ask_bid_ratio >= ask_bid_threshold,
            'value': f"{ask_bid_ratio:.2f}",
            'threshold': f"{ask_bid_threshold}"
        }

        if ask_bid_ratio < ask_bid_threshold:
            if should_log:
                logger.info(f"  [3] ❌ 五檔條件: 失敗 | 委賣/委買比 {ask_bid_ratio:.2f} < {ask_bid_threshold}")
            signal.failure_reason = "五檔條件不足"
            self.entry_signals.append(signal)
            return signal
        elif should_log:
            logger.info(f"  [3] ✓ 五檔條件: 通過 | 委賣/委買比 {ask_bid_ratio:.2f}")

        # 4. 大量搓合條件
        if self.config.get('massive_matching_enabled', True):
            # 判斷使用動態門檻或固定門檻
            use_dynamic = self.config.get('use_dynamic_liquidity_threshold', False)
            threshold_df = self.config.get('dynamic_liquidity_threshold_df', None)

            if use_dynamic and threshold_df is not None:
                # 嘗試從parquet檔案查找動態門檻
                try:
                    current_date = current_time.date()
                    # 將日期轉換為pandas的datetime格式用於查找
                    date_str = pd.Timestamp(current_date)

                    # 查找該日期、該股票的門檻
                    if date_str in threshold_df.index and stock_id in threshold_df.columns:
                        base_amount = float(threshold_df.loc[date_str, stock_id])
                        # 乘以係數得到實際門檻
                        multiplier = self.config.get('dynamic_liquidity_multiplier', 0.004)
                        massive_threshold = base_amount * multiplier

                        # 套用上限（預設 5000 萬）
                        threshold_cap = self.config.get('dynamic_liquidity_threshold_cap', 50_000_000)
                        if massive_threshold > threshold_cap:
                            massive_threshold = threshold_cap
                            threshold_source = f"動態(係數={multiplier}, 上限={threshold_cap/1e6:.0f}M)"
                        else:
                            threshold_source = f"動態(係數={multiplier})"
                    else:
                        # 找不到，使用固定門檻
                        massive_threshold = self.config.get('massive_matching_amount', 1000000.0)
                        threshold_source = "固定(找不到動態值)"
                except Exception as e:
                    logger.warning(f"查找動態門檻失敗: {e}，使用固定門檻")
                    massive_threshold = self.config.get('massive_matching_amount', 1000000.0)
                    threshold_source = "固定(查找失敗)"
            else:
                # 使用固定門檻
                massive_threshold = self.config.get('massive_matching_amount', 1000000.0)
                threshold_source = "固定"

            massive_matching_m = massive_matching_amount / 1000000
            threshold_m = massive_threshold / 1000000

            signal.conditions['大量搓合'] = {
                'enabled': True,
                'pass': massive_matching_amount >= massive_threshold,
                'value': f"{massive_matching_m:.2f}M",
                'threshold': f"{threshold_m:.1f}M",
                'source': threshold_source
            }

            if massive_matching_amount < massive_threshold:
                if should_log:
                    logger.info(f"  [4] ❌ 大量搓合: 失敗 | {massive_matching_m:.2f}M < {threshold_m:.1f}M ({threshold_source})")
                signal.failure_reason = "大量搓合不足"
                self.entry_signals.append(signal)
                return signal
            elif should_log:
                logger.info(f"  [4] ✓ 大量搓合: 通過 | {massive_matching_m:.2f}M >= {threshold_m:.1f}M ({threshold_source})")
        else:
            signal.conditions['大量搓合'] = {'enabled': False}
            if should_log:
                logger.info(f"  [4] - 大量搓合: 已停用")

        # 4b. 外盤金額比較（重複進場專用）
        if min_outside_amount is not None:
            signal.conditions['外盤金額比較'] = {
                'pass': massive_matching_amount > min_outside_amount,
                'value': f"{massive_matching_amount/1000000:.2f}M",
                'threshold': f"{min_outside_amount/1000000:.2f}M"
            }
            if massive_matching_amount <= min_outside_amount:
                if should_log:
                    logger.info(f"  [4b] ❌ 外盤金額比較: 失敗 | {massive_matching_amount/1000000:.2f}M ≤ {min_outside_amount/1000000:.2f}M")
                signal.failure_reason = "外盤金額不足（重複進場）"
                self.entry_signals.append(signal)
                return signal
            elif should_log:
                logger.info(f"  [4b] ✓ 外盤金額比較: 通過 | {massive_matching_amount/1000000:.2f}M")

        # 5. Ratio 條件
        if self.config.get('ratio_entry_enabled', True):
            ratio = indicators.get('ratio', 0.0)
            ratio = 0.0 if ratio is None or pd.isna(ratio) else float(ratio)

            # OR 邏輯檢查：ratio > 固定門檻 OR ratio > 動態門檻
            if dynamic_ratio_threshold is not None and fixed_ratio_threshold is not None:
                ratio_pass = (ratio > fixed_ratio_threshold) or (ratio > dynamic_ratio_threshold)

                signal.conditions['Ratio條件'] = {
                    'enabled': True,
                    'pass': ratio_pass,
                    'value': f"{ratio:.2f}",
                    'threshold': f">{fixed_ratio_threshold:.2f} 或 >{dynamic_ratio_threshold:.2f}"
                }

                if not ratio_pass:
                    if should_log:
                        logger.info(f"  [5] ❌ Ratio條件(15s/300s): 失敗 | {ratio:.2f} 不滿足條件")
                    signal.failure_reason = "Ratio條件不足"
                    self.entry_signals.append(signal)
                    return signal
                elif should_log:
                    logger.info(f"  [5] ✓ Ratio條件(15s/300s): 通過 | {ratio:.2f}")
            else:
                default_threshold = self.config.get('ratio_entry_threshold', 3.0)
                signal.conditions['Ratio條件'] = {
                    'enabled': True,
                    'pass': ratio >= default_threshold,
                    'value': f"{ratio:.2f}",
                    'threshold': f"{default_threshold:.2f}"
                }

                if ratio < default_threshold:
                    if should_log:
                        logger.info(f"  [5] ❌ Ratio條件(15s/300s): 失敗 | {ratio:.2f} < {default_threshold:.2f}")
                    signal.failure_reason = "Ratio條件不足"
                    self.entry_signals.append(signal)
                    return signal
                elif should_log:
                    logger.info(f"  [5] ✓ Ratio條件(15s/300s): 通過 | {ratio:.2f}")
        else:
            signal.conditions['Ratio條件'] = {'enabled': False}
            if should_log:
                logger.info(f"  [5] - Ratio條件(15s/300s): 已停用")

        # 6. 區間漲幅濾網
        if self.config.get('interval_pct_filter_enabled', False):
            interval_minutes = self.config.get('interval_pct_minutes', 3)
            interval_threshold = self.config.get('interval_pct_threshold', 4.0)
            pct_key = f'pct_{interval_minutes}min'
            interval_pct = indicators.get(pct_key, 0.0)
            interval_pct = 0.0 if interval_pct is None or pd.isna(interval_pct) else float(interval_pct)

            signal.conditions['區間漲幅'] = {
                'enabled': True,
                'pass': interval_pct <= interval_threshold,
                'value': f"{interval_pct:.2f}%",
                'threshold': f"{interval_threshold}%",
                'minutes': interval_minutes
            }

            if interval_pct > interval_threshold:
                if should_log:
                    logger.info(f"  [6] ❌ 區間漲幅: 失敗 | {interval_minutes}分鐘漲 {interval_pct:.2f}% > {interval_threshold}%")
                signal.failure_reason = f"{interval_minutes}分鐘漲幅過大"
                self.entry_signals.append(signal)
                return signal
            elif should_log:
                logger.info(f"  [6] ✓ 區間漲幅: 通過 | {interval_minutes}分鐘漲 {interval_pct:.2f}%")
        else:
            signal.conditions['區間漲幅'] = {'enabled': False}
            if should_log:
                logger.info(f"  [6] - 區間漲幅: 已停用")

        # 7. 低點檢查（支援3分鐘、10分鐘、15分鐘）
        # 7.1 3分鐘低點檢查
        if self.config.get('low_3min_filter_enabled', False):
            low_3min_threshold = self.config.get('low_3min_threshold', 2.0)
            low_3min = indicators.get('low_3min', current_price)

            # 計算相對3分鐘低點的漲幅
            if low_3min > 0:
                price_from_low_pct = ((current_price - low_3min) / low_3min) * 100
            else:
                price_from_low_pct = 0.0

            signal.conditions['3分鐘低點'] = {
                'enabled': True,
                'pass': price_from_low_pct <= low_3min_threshold,
                'value': f"{price_from_low_pct:.2f}%",
                'threshold': f"{low_3min_threshold}%",
                'low_3min': f"{low_3min:.2f}"
            }

            if price_from_low_pct > low_3min_threshold:
                if should_log:
                    logger.info(f"  [7] ❌ 3分鐘低點: 失敗 | 相對低點漲 {price_from_low_pct:.2f}% > {low_3min_threshold}% (低點:{low_3min:.2f})")
                signal.failure_reason = "距離3分鐘低點過高"
                self.entry_signals.append(signal)
                return signal
            elif should_log:
                logger.info(f"  [7] ✓ 3分鐘低點: 通過 | 相對低點漲 {price_from_low_pct:.2f}% (低點:{low_3min:.2f})")

        # 7.2 10分鐘低點檢查
        elif self.config.get('low_10min_filter_enabled', False):
            low_10min_threshold = self.config.get('low_10min_threshold', 4.0)
            low_10min = indicators.get('low_10min', current_price)

            # 計算相對10分鐘低點的漲幅
            if low_10min > 0:
                price_from_low_pct = ((current_price - low_10min) / low_10min) * 100
            else:
                price_from_low_pct = 0.0

            signal.conditions['10分鐘低點'] = {
                'enabled': True,
                'pass': price_from_low_pct <= low_10min_threshold,
                'value': f"{price_from_low_pct:.2f}%",
                'threshold': f"{low_10min_threshold}%",
                'low_10min': f"{low_10min:.2f}"
            }

            if price_from_low_pct > low_10min_threshold:
                if should_log:
                    logger.info(f"  [7] ❌ 10分鐘低點: 失敗 | 相對低點漲 {price_from_low_pct:.2f}% > {low_10min_threshold}% (低點:{low_10min:.2f})")
                signal.failure_reason = "距離10分鐘低點過高"
                self.entry_signals.append(signal)
                return signal
            elif should_log:
                logger.info(f"  [7] ✓ 10分鐘低點: 通過 | 相對低點漲 {price_from_low_pct:.2f}% (低點:{low_10min:.2f})")

        # 7.3 15分鐘低點檢查
        elif self.config.get('low_15min_filter_enabled', False):
            low_15min_threshold = self.config.get('low_15min_threshold', 4.0)
            low_15min = indicators.get('low_15min', current_price)

            # 計算相對15分鐘低點的漲幅
            if low_15min > 0:
                price_from_low_pct = ((current_price - low_15min) / low_15min) * 100
            else:
                price_from_low_pct = 0.0

            signal.conditions['15分鐘低點'] = {
                'enabled': True,
                'pass': price_from_low_pct <= low_15min_threshold,
                'value': f"{price_from_low_pct:.2f}%",
                'threshold': f"{low_15min_threshold}%",
                'low_15min': f"{low_15min:.2f}"
            }

            if price_from_low_pct > low_15min_threshold:
                if should_log:
                    logger.info(f"  [7] ❌ 15分鐘低點: 失敗 | 相對低點漲 {price_from_low_pct:.2f}% > {low_15min_threshold}% (低點:{low_15min:.2f})")
                signal.failure_reason = "距離15分鐘低點過高"
                self.entry_signals.append(signal)
                return signal
            elif should_log:
                logger.info(f"  [7] ✓ 15分鐘低點: 通過 | 相對低點漲 {price_from_low_pct:.2f}% (低點:{low_15min:.2f})")
        else:
            signal.conditions['低點檢查'] = {'enabled': False}
            if should_log:
                logger.info(f"  [7] - 低點檢查: 已停用")

        # 8. 非漲停價（漲幅 < 10%）
        signal.conditions['非漲停價'] = {
            'pass': price_change_pct < 10.0,
            'value': f"{price_change_pct:.2f}%",
            'threshold': '10%'
        }

        if price_change_pct >= 10.0:
            if should_log:
                logger.info(f"  [8] ❌ 非漲停價: 失敗 | 漲幅 {price_change_pct:.2f}% >= 10%")
            signal.failure_reason = "接近漲停"
            self.entry_signals.append(signal)
            return signal
        elif should_log:
            logger.info(f"  [8] ✓ 非漲停價: 通過")

        # 9. 突破品質檢查（新策略）或小單推升過濾（舊策略）
        if self.small_order_filter.breakout_quality_enabled and current_row is not None and is_day_high_breakout:
            # 使用突破品質檢查（傳入 df 以檢查歷史大單）
            is_valid, reason = self.small_order_filter.check_breakout_quality(current_row, is_day_high_breakout, df)

            signal.conditions['突破品質'] = {
                'enabled': True,
                'pass': is_valid,
                'reason': reason
            }

            if not is_valid:
                if should_log:
                    logger.info(f"  [9] ❌ 突破品質: 失敗 | {reason}")
                signal.failure_reason = f"突破品質不足: {reason}"
                self.entry_signals.append(signal)
                return signal
            elif should_log:
                logger.info(f"  [9] ✓ 突破品質: 通過 | {reason}")

        elif self.small_order_filter.enabled and df is not None:
            # 使用舊的小單過濾（如果啟用）
            is_valid, reason = self.small_order_filter.check_small_order_pattern(df, current_time)

            signal.conditions['小單過濾'] = {
                'enabled': True,
                'pass': is_valid,
                'reason': reason
            }

            if not is_valid:
                if should_log:
                    logger.info(f"  [9] ❌ 小單過濾: 失敗 | {reason}")
                signal.failure_reason = f"小單推升: {reason}"
                self.entry_signals.append(signal)
                return signal
            elif should_log:
                logger.info(f"  [9] ✓ 小單過濾: 通過 | {reason}")
        else:
            signal.conditions['突破品質'] = {'enabled': False}
            signal.conditions['小單過濾'] = {'enabled': False}
            if should_log:
                if not self.small_order_filter.breakout_quality_enabled and not self.small_order_filter.enabled:
                    logger.info(f"  [9] - 突破品質/小單過濾: 已停用")
                elif not is_day_high_breakout:
                    logger.info(f"  [9] - 突破品質: 非突破時機，跳過檢查")
                elif current_row is None and df is None:
                    logger.info(f"  [9] - 突破品質/小單過濾: 無資料")

        # 所有條件通過
        signal.passed = True
        signal.failure_reason = ""
        self.entry_signals.append(signal)

        if should_log:
            logger.info(f"{'='*60}")
            logger.info(f"進場檢查結果: ✓ 所有條件通過！")
            logger.info(f"{'='*60}")

        return signal

    def get_entry_signals_summary(self) -> pd.DataFrame:
        """
        取得所有進場訊號的統計摘要

        Returns:
            進場訊號統計 DataFrame
        """
        if not self.entry_signals:
            return pd.DataFrame()

        # 轉換為 DataFrame
        signals_data = [signal.to_dict() for signal in self.entry_signals]
        df = pd.DataFrame(signals_data)

        # 統計各條件失敗次數
        failure_stats = {}
        for signal in self.entry_signals:
            if not signal.passed and signal.failure_reason:
                failure_stats[signal.failure_reason] = failure_stats.get(signal.failure_reason, 0) + 1

        logger.info("\n" + "="*60)
        logger.info("進場訊號統計摘要")
        logger.info("="*60)
        logger.info(f"總檢查次數: {len(self.entry_signals)}")
        logger.info(f"成功進場次數: {df['passed'].sum()}")
        logger.info(f"進場成功率: {df['passed'].sum() / len(self.entry_signals) * 100:.1f}%")

        if failure_stats:
            logger.info("\n失敗原因統計:")
            for reason, count in sorted(failure_stats.items(), key=lambda x: x[1], reverse=True):
                logger.info(f"  {reason}: {count} 次 ({count/len(self.entry_signals)*100:.1f}%)")

        return df
