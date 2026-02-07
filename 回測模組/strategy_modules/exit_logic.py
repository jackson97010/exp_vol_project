"""
出場邏輯模組
負責動能衰竭與掛單結構平衡停利法的出場邏輯
"""

import logging
from datetime import datetime
from typing import Dict, Optional

logger = logging.getLogger(__name__)


class ExitManager:
    """出場管理器"""

    def __init__(self, config: Dict):
        """
        初始化出場管理器

        Args:
            config: 出場設定字典
        """
        self.config = config
        # 讀取移動停利配置
        self.trailing_stop_config = config.get('trailing_stop', {})
        self.trailing_stop_enabled = self.trailing_stop_config.get('enabled', False)
        self.trailing_stop_levels = self.trailing_stop_config.get('levels', [])
        self.entry_price_protection = self.trailing_stop_config.get('entry_price_protection', True)


    def _get_tick_size(self, price: float) -> float:
        """取得價格的 tick 大小"""
        if price >= 1000:
            return 5.0
        elif price >= 500:
            return 1.0
        elif price >= 100:
            return 0.5
        elif price >= 50:
            return 0.1
        elif price >= 10:
            return 0.05
        else:
            return 0.01

    def _add_ticks(self, price: float, ticks: int) -> float:
        """將價格加上指定的 tick 數"""
        tick_size = self._get_tick_size(price)
        return price + (tick_size * ticks)

    def check_hard_stop_loss(
        self,
        position,
        current_price: float,
        current_time: datetime
    ) -> Optional[Dict]:
        """
        檢查硬停損條件：Day High 往下 N ticks

        Args:
            position: 持倉物件
            current_price: 當前價格
            current_time: 當前時間

        Returns:
            出場結果字典，若無需出場則返回 None
        """
        # 根據 tick size 決定停損 ticks 數
        entry_tick_size = self._get_tick_size(position.day_high_at_entry)

        if float(entry_tick_size) in (0.5, 5.0):
            stop_loss_ticks = self.config.get('strategy_b_stop_loss_ticks_large', 3)
            tick_type = "large"
        else:
            stop_loss_ticks = self.config.get('strategy_b_stop_loss_ticks_small', 4)
            tick_type = "small"

        stop_loss_price = self._add_ticks(position.day_high_at_entry, -stop_loss_ticks)

        if current_price <= stop_loss_price:
            logger.info(
                f"[硬停損] 時間: {current_time}, 價格: {current_price:.2f}, 停損價: {stop_loss_price:.2f} "
                f"(Day High={position.day_high_at_entry:.2f} 往下 {stop_loss_ticks} ticks, tick_type={tick_type})"
            )

            return {
                'exit_type': 'remaining',
                'exit_reason': 'tick_stop_loss',
                'exit_price': current_price,
                'exit_time': current_time,
                'stop_loss_price': stop_loss_price,
                'stop_loss_ticks': stop_loss_ticks,
                'tick_type': tick_type
            }

        return None

    def check_limit_up_exit(
        self,
        position,
        current_price: float,
        current_time: datetime,
        limit_up_price: float
    ) -> Optional[Dict]:
        """
        檢查漲停價出場條件

        Args:
            position: 持倉物件
            current_price: 當前價格
            current_time: 當前時間
            limit_up_price: 漲停價

        Returns:
            出場結果字典，若無需出場則返回 None
        """
        if current_price >= limit_up_price:
            logger.info(f"[第一階段出場] 時間: {current_time}, 價格: {current_price:.2f}, 原因: 觸及漲停價")

            return {
                'exit_type': 'partial',
                'exit_ratio': 0.5,
                'exit_reason': '觸及漲停價（直接出場）',
                'exit_price': limit_up_price,
                'exit_time': current_time
            }

        return None

    def check_momentum_exhaustion(
        self,
        position,
        row,
        momentum_tracker,
        orderbook_monitor,
        current_time: datetime,
        current_price: float
    ) -> Optional[Dict]:
        """
        檢查動能衰竭出場條件（第一階段：減碼50%）

        Args:
            position: 持倉物件
            row: 當前資料列
            momentum_tracker: 動能追蹤器（從 Trade 資料計算增長率）
            orderbook_monitor: 掛單監控器（從 Depth 資料取得掛單平衡度）
            current_time: 當前時間
            current_price: 當前價格

        Returns:
            出場結果字典，若無需出場則返回 None
        """
        # 如果已經執行過部分出場，不再檢查第一階段
        if position.partial_exit_done:
            return None

        # 檢查持倉時間（至少持倉 1 分鐘才考慮出場）
        holding_seconds = (current_time - position.entry_time).total_seconds()
        if holding_seconds < 60:  # 1 分鐘 = 60 秒
            return None

        # 條件1：Day High 增長率 < 0.86%（從 Trade 資料的 day_high 計算）
        growth_rate = momentum_tracker.get_growth_rate()
        momentum_slowing = growth_rate < 0.0086

        # 條件2：掛單平衡度（買/賣 < 1.0）連續 5 個 tick
        # 注意：掛單資料來自 Depth，透過 ffill 填充到 Trade 資料行中
        balance_ratio = orderbook_monitor.calculate_balance_ratio(row)
        buy_weak = balance_ratio < 1

        if not hasattr(position, 'buy_weak_streak'):
            position.buy_weak_streak = 0
        position.buy_weak_streak = position.buy_weak_streak + 1 if buy_weak else 0
        buy_weak_5ticks = position.buy_weak_streak >= 5

        # 條件3：Day High 增長率從峰值回落 >= 0.5%
        growth_drawdown = max(momentum_tracker.peak_growth_rate - growth_rate, 0.0)
        growth_drawdown_trigger = growth_drawdown >= 0.005

        # OR 條件：(條件3 AND 條件2) OR 條件1
        should_exit = (growth_drawdown_trigger and buy_weak_5ticks) or momentum_slowing

        # 只在滿足出場條件時才記錄日誌
        if should_exit:
            reason_parts = []
            if growth_drawdown_trigger and buy_weak_5ticks:
                reason_parts.append("成長率峰值回落+掛單弱(5tick)")
            if momentum_slowing:
                reason_parts.append("成長率<0.86%")

            reason_text = " / ".join(reason_parts)

            logger.info(
                f"[第一階段出場] 時間: {current_time}, 價格: {current_price:.2f}, "
                f"DH增長率: {growth_rate:.2%}, 掛單平衡度(買/賣): {balance_ratio:.2f}, "
                f"原因: {reason_text}"
            )

            return {
                'exit_type': 'partial',
                'exit_ratio': 0.5,
                'exit_reason': f'動能衰竭停利（{reason_text}）',
                'exit_price': current_price,
                'exit_time': current_time,
                'growth_rate': growth_rate,
                'balance_ratio': balance_ratio,
                'buy_weak_streak': position.buy_weak_streak,
                'growth_drawdown': growth_drawdown
            }

        return None

    def check_partial_exit(
        self,
        position,
        row,
        momentum_tracker,
        orderbook_monitor,
        limit_up_price: float
    ) -> Optional[Dict]:
        """
        檢查第一階段出場條件（減碼50%）

        Args:
            position: 持倉物件
            row: 當前資料列
            momentum_tracker: 動能追蹤器
            orderbook_monitor: 掛單監控器
            limit_up_price: 漲停價

        Returns:
            出場結果字典，若無需出場則返回 None
        """
        current_time = row['time']
        current_price = row['price']

        # 更新最高價
        if current_price > position.highest_price:
            position.highest_price = current_price

        # 如果已經部分出場，不再檢查第一階段
        if position.partial_exit_done:
            return None

        # 1. 硬停損檢查
        stop_loss_result = self.check_hard_stop_loss(position, current_price, current_time)
        if stop_loss_result:
            return stop_loss_result

        # 2. 漲停價出場檢查
        limit_up_result = self.check_limit_up_exit(position, current_price, current_time, limit_up_price)
        if limit_up_result:
            return limit_up_result

        # 3. 動能衰竭出場檢查
        momentum_result = self.check_momentum_exhaustion(
            position, row, momentum_tracker, orderbook_monitor, current_time, current_price
        )
        if momentum_result:
            return momentum_result

        return None

    def check_final_exit(
        self,
        position,
        row,
        current_time: datetime,
        current_price: float
    ) -> Optional[Dict]:
        """
        檢查第二階段出場條件（清倉）

        Args:
            position: 持倉物件
            row: 當前資料列
            current_time: 當前時間
            current_price: 當前價格

        Returns:
            出場結果字典，若無需出場則返回 None
        """
        # 只有在已經部分出場後才檢查清倉條件
        if not position.partial_exit_done:
            return None

        # 取得 3 分鐘低點和進場價
        low_3m = row.get('low_3m', None)
        low_3m_valid = low_3m is not None and low_3m > 0
        entry_price = position.entry_price
        entry_price_valid = entry_price is not None and entry_price > 0

        # 決定出場門檻
        if low_3m_valid and entry_price_valid and low_3m > entry_price:
            exit_threshold = low_3m
            reason_text = f"跌破3m低點{low_3m:.2f}"
        elif entry_price_valid:
            exit_threshold = entry_price
            reason_text = "回到進場價"
        else:
            return None

        # 檢查是否跌破門檻
        if current_price <= exit_threshold:
            logger.info(f"[第二階段出場] 時間: {current_time}, 價格: {current_price:.2f}, {reason_text}")

            return {
                'exit_type': 'remaining',
                'exit_reason': f"清倉（{reason_text}）",
                'exit_price': current_price,
                'exit_time': current_time,
                'exit_threshold': exit_threshold
            }

        return None

    def check_reentry_stop_loss(
        self,
        position,
        row,
        momentum_tracker,
        orderbook_monitor
    ) -> Optional[Dict]:
        """
        檢查回補停損條件（使用動能衰竭邏輯）

        Args:
            position: 持倉物件
            row: 當前資料列
            momentum_tracker: 動能追蹤器
            orderbook_monitor: 掛單監控器

        Returns:
            出場結果字典，若無需出場則返回 None
        """
        if not position.partial_exit_recovered:
            return None

        current_price = row['price']
        current_time = row['time']

        # 1. 價格停損
        if position.reentry_stop_price and current_price <= position.reentry_stop_price:
            logger.info(
                f"[回補停損] 時間: {current_time}, 價格: {current_price:.2f}, "
                f"停損價: {position.reentry_stop_price:.2f}"
            )
            return {
                'exit_type': 'reentry_stop',
                'exit_reason': '回補停損',
                'exit_price': current_price,
                'exit_time': current_time
            }

        # 2. 動能衰竭停損（回補後30秒開始檢查）
        if position.reentry_time:
            reentry_elapsed = (current_time - position.reentry_time).total_seconds()

            # 30秒後開始檢查動能衰竭
            if reentry_elapsed >= 30.0:
                # 使用與第一階段出場相同的動能衰竭邏輯
                growth_rate = momentum_tracker.get_growth_rate()
                balance_ratio = orderbook_monitor.calculate_balance_ratio(row)
                growth_drawdown = momentum_tracker.get_growth_drawdown()

                # 更新委買弱勢追蹤
                buy_weak = balance_ratio < 1.0
                if not hasattr(position, 'buy_weak_streak'):
                    position.buy_weak_streak = 0
                position.buy_weak_streak = position.buy_weak_streak + 1 if buy_weak else 0

                # 條件A：DH成長率 < 0.86%
                condition_a = growth_rate < 0.0086

                # 條件B：growth_drawdown >= 0.5% 且連續5個tick委買弱勢
                condition_b = growth_drawdown >= 0.005 and position.buy_weak_streak >= 5

                if condition_a or condition_b:
                    reason_text = []
                    if condition_a:
                        reason_text.append(f"成長率<0.86%")
                    if condition_b:
                        reason_text.append(f"回撤≥0.5%且委買弱勢")

                    logger.info(
                        f"[回補動能衰竭] 時間: {current_time}, 價格: {current_price:.2f}, "
                        f"經過時間: {reentry_elapsed:.1f}秒, 成長率: {growth_rate:.2%}, "
                        f"原因: {' & '.join(reason_text)}"
                    )

                    # 設定動能停損已觸發標記
                    position.reentry_momentum_stop_triggered = True
                    position.reentry_momentum_stop_time = current_time

                    return {
                        'exit_type': 'reentry_stop',
                        'exit_reason': f'回補動能衰竭（{" & ".join(reason_text)}）',
                        'exit_price': current_price,
                        'exit_time': current_time,
                        'elapsed_seconds': reentry_elapsed,
                        'growth_rate': growth_rate,
                        'balance_ratio': balance_ratio,
                        'growth_drawdown': growth_drawdown
                    }

        # 3. 動能停損後跌破回補價格出場
        if hasattr(position, 'reentry_momentum_stop_triggered') and position.reentry_momentum_stop_triggered:
            if position.reentry_price and current_price < position.reentry_price:
                logger.info(
                    f"[回補後跌破進場價] 時間: {current_time}, 價格: {current_price:.2f}, "
                    f"回補價: {position.reentry_price:.2f}"
                )
                return {
                    'exit_type': 'reentry_stop',
                    'exit_reason': '動能停損後跌破回補價',
                    'exit_price': current_price,
                    'exit_time': current_time
                }

        return None

    def check_trailing_stop(
        self,
        position,
        row,
        current_time: datetime,
        current_price: float
    ) -> Optional[Dict]:
        """
        檢查移動停利條件

        Args:
            position: 持倉物件
            row: 當前資料列
            current_time: 當前時間
            current_price: 當前價格

        Returns:
            出場結果字典，若無需出場則返回 None
        """
        # 如果未啟用移動停利，直接返回
        if not self.trailing_stop_enabled:
            return None

        # 如果剩餘部位已經很少，不再執行移動停利
        if position.remaining_ratio <= 0.1:
            return None

        # 根據配置檢查各時間框架的低點
        for level_config in self.trailing_stop_levels:
            level_name = level_config.get('name')
            field_name = level_config.get('field')
            exit_ratio = level_config.get('exit_ratio', 0.333)

            # 跳過已觸發的級別
            if position.exit_levels_triggered.get(level_name, False):
                continue

            # 獲取低點價格
            low_price = row.get(field_name, None)

            # 檢查低點是否有效
            if low_price is None or low_price <= 0:
                continue

            # 檢查是否觸及或跌破低點
            if current_price <= low_price:
                # 標記此級別已觸發（在 PositionManager 中處理）
                logger.info(
                    f"[移動停利觸發] 時間: {current_time}, 價格: {current_price:.2f}, "
                    f"級別: {level_name}, 低點: {low_price:.2f}"
                )

                return {
                    'exit_type': 'trailing_stop',
                    'exit_ratio': exit_ratio,
                    'exit_level': level_name,
                    'exit_price': current_price,
                    'exit_time': current_time,
                    'exit_reason': f'觸及{level_name}低點({low_price:.2f})'
                }

        return None

    def check_entry_price_protection(
        self,
        position,
        current_price: float,
        current_time: datetime
    ) -> Optional[Dict]:
        """
        檢查進場價保護（當已有部分出場後，跌破進場價全部出場）
        特別針對早盤策略：第一批出場後就啟動進場價保護

        Args:
            position: 持倉物件
            current_price: 當前價格
            current_time: 當前時間

        Returns:
            出場結果字典，若無需出場則返回 None
        """
        # 如果未啟用進場價保護，直接返回
        if not self.entry_price_protection:
            return None

        # 只有在已部分出場後才檢查（剩餘部位小於100%）
        if position.remaining_ratio >= 1.0:
            return None

        # 如果剩餘部位為0，不需要再出場
        if position.remaining_ratio <= 0:
            return None

        # 判斷是否為早盤策略且已經第一批出場（剩餘部位約66.7%）
        is_early_mode = self.config.get('entry_mode') in ['early_0901', 'early_0905']
        is_first_batch_exited = position.remaining_ratio <= 0.67 and position.remaining_ratio > 0.33

        # 早盤策略：第一批出場後立即啟動進場價保護
        if is_early_mode and is_first_batch_exited:
            if current_price <= position.entry_price:
                logger.info(
                    f"[早盤策略進場價保護] 時間: {current_time}, 價格: {current_price:.2f}, "
                    f"進場價: {position.entry_price:.2f}, 剩餘部位: {position.remaining_ratio:.1%}"
                )

                return {
                    'exit_type': 'protection',
                    'exit_ratio': position.remaining_ratio,  # 全部剩餘部位
                    'exit_price': current_price,
                    'exit_time': current_time,
                    'exit_reason': f'早盤策略跌破進場價保護({position.entry_price:.2f})'
                }
        # 一般策略：任何部分出場後都檢查
        elif not is_early_mode:
            if current_price <= position.entry_price:
                logger.info(
                    f"[進場價保護] 時間: {current_time}, 價格: {current_price:.2f}, "
                    f"進場價: {position.entry_price:.2f}, 剩餘部位: {position.remaining_ratio:.1%}"
                )

                return {
                    'exit_type': 'protection',
                    'exit_ratio': position.remaining_ratio,  # 全部剩餘部位
                    'exit_price': current_price,
                    'exit_time': current_time,
                    'exit_reason': f'跌破進場價保護({position.entry_price:.2f})'
                }

        return None

    def check_new_exit_logic(
        self,
        position,
        row,
        momentum_tracker,
        orderbook_monitor,
        limit_up_price: float
    ) -> Optional[Dict]:
        """
        新的出場邏輯檢查（整合所有出場條件）

        Args:
            position: 持倉物件
            row: 當前資料列
            momentum_tracker: 動能追蹤器
            orderbook_monitor: 掛單監控器
            limit_up_price: 漲停價

        Returns:
            出場結果字典，若無需出場則返回 None
        """
        current_time = row['time']
        current_price = row['price']

        # 更新最高價
        if current_price > position.highest_price:
            position.highest_price = current_price

        # 檢查回補停損（如果有回補進場）
        if position.partial_exit_recovered:
            reentry_stop_result = self.check_reentry_stop_loss(position, row, momentum_tracker, orderbook_monitor)
            if reentry_stop_result:
                return reentry_stop_result

        # 如果啟用了移動停利，則不使用原有的動能衰竭邏輯
        if not self.trailing_stop_enabled:
            # 第一階段：減碼 50%
            if not position.partial_exit_done:
                partial_exit_result = self.check_partial_exit(
                    position, row, momentum_tracker, orderbook_monitor, limit_up_price
                )
                if partial_exit_result:
                    return partial_exit_result

            # 第二階段：清倉
            else:
                final_exit_result = self.check_final_exit(position, row, current_time, current_price)
                if final_exit_result:
                    return final_exit_result
        else:
            # 使用移動停利時，檢查漲停價出場和硬停損
            if position.remaining_ratio > 0:
                # 1. 漲停價出場檢查（優先）
                limit_up_result = self.check_limit_up_exit(position, current_price, current_time, limit_up_price)
                if limit_up_result:
                    # 調整出場比例為剩餘部位的一半（保持原有邏輯）
                    if position.remaining_ratio == 1.0:
                        # 如果是全部位，出場50%
                        limit_up_result['exit_ratio'] = 0.5
                    else:
                        # 如果已有部分出場，出場剩餘部位
                        limit_up_result['exit_ratio'] = position.remaining_ratio
                        limit_up_result['exit_type'] = 'remaining'
                    return limit_up_result

                # 2. 硬停損檢查
                stop_loss_result = self.check_hard_stop_loss(position, current_price, current_time)
                if stop_loss_result:
                    # 調整出場比例為剩餘部位
                    stop_loss_result['exit_ratio'] = position.remaining_ratio
                    stop_loss_result['exit_type'] = 'protection'
                    return stop_loss_result

        return None
