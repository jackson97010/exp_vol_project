import logging
import pandas as pd
from datetime import datetime
from typing import Dict, List, Optional
from strategy_modules.position_manager import Position, TradeRecord
from strategy_modules.entry_logic import EntrySignal
from core.constants import *
logger = logging.getLogger(__name__)

class BacktestLoop:
    """Backtest main loop handler."""
    def __init__(self, engine):
        """
        Args:
            engine: BacktestEngine instance
        """
        self.engine = engine
        self.entry_checker = engine.entry_checker
        self.exit_manager = engine.exit_manager
        self.position_manager = engine.position_manager
        self.reentry_manager = engine.reentry_manager
        # Indicators
        self.momentum_tracker = engine.momentum_tracker
        self.orderbook_monitor = engine.orderbook_monitor
        self.outside_volume_tracker = engine.outside_volume_tracker
        self.massive_matching_tracker = engine.massive_matching_tracker
        self.inside_outside_ratio_tracker = engine.inside_outside_ratio_tracker
        self.large_order_io_ratio_tracker = engine.large_order_io_ratio_tracker
        # Configs
        self.entry_config = engine.entry_config
        self.exit_config = engine.exit_config
        self.reentry_config = engine.reentry_config
        self.last_exit_was_stop_loss = False
        
    def run(self, df: pd.DataFrame, stock_id: str, ref_price: float, limit_up_price: float) -> List[TradeRecord]:
        """
        Run backtest loop.
        Returns:
            TradeRecord list
        """
        state = self._init_loop_state()
        metrics = self._init_metrics_lists()
        columns = list(df.columns)
        for row_tuple in df.itertuples(index=False, name=None):
            row = dict(zip(columns, row_tuple))
            self._update_current_state(row, state)
            self._update_indicators(row, state)
            self._calculate_metrics(row, state, metrics)
            # 取得當前價格
            current_price = row.get('price', 0)
            if self.position_manager.has_position():
                self._process_exit_logic(state, row, limit_up_price)
            else:
                # 進場邏輯只處理成交資料 (price > 0)，忽略五檔資料 (price = 0)
                if current_price > 0:
                    self._process_entry_logic(row, stock_id, ref_price, limit_up_price, state, df)
            # 只有成交資料才更新 prev_day_high，避免五檔資料污染
            if current_price > 0:
                state['prev_day_high'] = state['current_day_high']
        self._force_close_at_market_close(state)
        self._add_metrics_to_dataframe(df, metrics)
        return self.position_manager.trade_history

    def _init_loop_state(self) -> Dict:
        """Initialize loop state."""
        return {
            'prev_day_high': 0,
            'last_entry_time': None,
            'last_exit_time': None,  # 追蹤最後出場時間
            'day_high_break_count': 0,
            'day_high_break_count_after_entry_time': 0,
            'last_bid_ask_ratio': None,
            'last_entry_ratio': 0.0,
            'dynamic_ratio_threshold': None,
            'fixed_ratio_threshold': None,
            'first_entry_outside_volume': None,
            'current_time': None,
            'current_price': 0,
            'current_day_high': 0,
            'current_tick_type': 0,
            'current_volume': 0,
            'breakout_buffer_state': {
                'active': False,
                'start_time': None,
                'day_high': 0.0,
                'checked': False
            },
            # 等待外盤進場狀態
            'waiting_for_outside_entry': {
                'active': False,
                'breakout_time': None,  # 突破發生的時間戳
                'breakout_day_high': 0.0,  # 突破時的 day_high
                'prev_day_high': 0.0  # 突破前的 day_high
            },
            'reentry_buffer_state': {
                'active': False,
                'start_time': None,
                'day_high': 0.0,
                'checked': False,
                'max_outside_volume': 0.0
            }
        }
    def _init_metrics_lists(self) -> Dict:
        """Initialize metric lists."""
        return {
            'day_high_growth_rates': [],
            'bid_avg_volumes': [],
            'ask_avg_volumes': [],
            'balance_ratios': [],
            'day_high_breakouts': [],
            'inside_outside_ratios': [],
            'outside_ratios': [],
            'large_order_io_ratios': [],
            'large_order_outside_ratios': []
        }
    def _update_current_state(self, row, state: Dict):
        """Update current state from row."""
        state['current_time'] = row['time']
        state['current_price'] = row['price']
        state['current_day_high'] = row.get('day_high', state['current_price'])
        state['current_tick_type'] = row.get('tick_type', 0)
        state['current_volume'] = row.get('volume', 0)
        current_bid_ask_ratio = row.get('bid_ask_ratio', None)
        if current_bid_ask_ratio is not None and pd.notna(current_bid_ask_ratio) and current_bid_ask_ratio > 0:
            state['last_bid_ask_ratio'] = float(current_bid_ask_ratio)
    def _update_indicators(self, row, state: Dict):
        """Update indicator trackers."""
        tick_type_value = pd.to_numeric(state['current_tick_type'], errors='coerce')
        self.outside_volume_tracker.update_trades(
            state['current_time'], tick_type_value,
            state['current_price'], state['current_volume']
        )
        self.massive_matching_tracker.update(
            state['current_time'], tick_type_value,
            state['current_price'], state['current_volume']
        )
        self.inside_outside_ratio_tracker.update(
            state['current_time'], tick_type_value,
            state['current_price'], state['current_volume']
        )
        self.large_order_io_ratio_tracker.update(
            state['current_time'], tick_type_value,
            state['current_price'], state['current_volume']
        )
        self.momentum_tracker.update(state['current_time'], state['current_day_high'])
    def _calculate_metrics(self, row, state: Dict, metrics: Dict):
        """Calculate and record metrics."""
        growth_rate = self.momentum_tracker.get_growth_rate()
        metrics['day_high_growth_rates'].append(growth_rate)
        bid_avg = self.orderbook_monitor.calculate_bid_thickness(row)
        ask_avg = self.orderbook_monitor.calculate_ask_thickness(row)
        balance_ratio = self.orderbook_monitor.calculate_balance_ratio(row)
        metrics['bid_avg_volumes'].append(bid_avg)
        metrics['ask_avg_volumes'].append(ask_avg)
        metrics['balance_ratios'].append(balance_ratio)
        io_ratio = self.inside_outside_ratio_tracker.get_ratio()
        outside_ratio = self.inside_outside_ratio_tracker.get_outside_ratio()
        metrics['inside_outside_ratios'].append(io_ratio)
        metrics['outside_ratios'].append(outside_ratio)
        large_io_ratio = self.large_order_io_ratio_tracker.get_ratio()
        large_outside_ratio = self.large_order_io_ratio_tracker.get_outside_ratio()
        metrics['large_order_io_ratios'].append(large_io_ratio)
        metrics['large_order_outside_ratios'].append(large_outside_ratio)
        is_breakout = self.entry_checker.check_day_high_breakout(
            state['current_day_high'], state['prev_day_high']
        )
        metrics['day_high_breakouts'].append(is_breakout)
    def _process_exit_logic(self, state: Dict, row, limit_up_price: float):
        """Handle exit logic."""
        position = self.position_manager.get_current_position()
        current_time = state['current_time']
        current_price = state['current_price']
        prev_highest_price = position.highest_price
        if current_price > position.highest_price:
            position.highest_price = current_price
            logger.debug(
                f"[?新?高價] ??: {current_time}, ??: {current_price:.2f}, ??: {prev_highest_price:.2f}"
            )
        # Limit-up exit (priority in all modes).
        if limit_up_price and current_price >= limit_up_price:
            exit_reason = "觸及漲停價（直接出場）"
            if position.remaining_ratio < 1.0:
                logger.info(f"[第二階段出場] 時間: {current_time}, 價格: {limit_up_price:.2f}, 原因: 觸及漲停價")
                exit_type = "remaining"
                exit_ratio = position.remaining_ratio
            else:
                logger.info(f"[第一階段出場] 時間: {current_time}, 價格: {limit_up_price:.2f}, 原因: 觸及漲停價")
                exit_type = "partial"
                exit_ratio = 0.5
            self._handle_exit(
                {
                    "exit_type": exit_type,
                    "exit_ratio": exit_ratio,
                    "exit_reason": exit_reason,
                    "exit_price": limit_up_price,
                    "exit_time": current_time,
                },
                current_time,
                state
            )
            return
        use_trailing_stop = self.exit_manager.trailing_stop_enabled
        # Trailing-stop mode: only 1/3/5min low exits.
        if use_trailing_stop:
            trailing_result = self.exit_manager.check_trailing_stop(
                position, row, current_time, current_price
            )
            if trailing_result:
                self._handle_trailing_exit(trailing_result)
                position = self.position_manager.get_current_position()
                # 如果移動停利已經把所有部位出掉，關閉倉位並保存交易記錄
                if position and position.remaining_ratio <= 0:
                    # 關閉倉位（計算平均出場價格並保存交易記錄）
                    self._close_position_completely(position, trailing_result, state)
                return
            protection_result = self.exit_manager.check_entry_price_protection(
                position, current_price, current_time
            )
            if protection_result:
                self._handle_exit(protection_result, current_time, state)
                return
        # Momentum half-exit mode (only if trailing-stop disabled).
        if not use_trailing_stop:
            position = self.position_manager.get_current_position()
            if position:
                exit_result = self.exit_manager.check_momentum_exhaustion(
                    position, row, self.momentum_tracker, self.orderbook_monitor,
                    current_time, current_price
                )
                if exit_result:
                    self._handle_exit(exit_result, current_time, state)
                    return
                exit_result = self.exit_manager.check_final_exit(
                    position, row, current_time, current_price
                )
                if exit_result:
                    self._handle_exit(exit_result, current_time, state)
                    return
        # Hard stop (both modes).
        position = self.position_manager.get_current_position()
        if position:
            exit_result = self.exit_manager.check_hard_stop_loss(
                position, current_price, current_time
            )
            if exit_result:
                self._handle_exit(exit_result, current_time, state)
        # Reentry check (only when enabled).
        position = self.position_manager.get_current_position()
        if (
            position
            and bool(self.reentry_config.get('reentry', False))
            and position.allow_reentry
            and position.remaining_ratio < 1.0
        ):
            self._check_reentry(position, row, state, prev_highest_price)
    def _process_entry_logic(self, row, stock_id: str, ref_price: float,
                            limit_up_price: float, state: Dict, df: pd.DataFrame):
        """Handle entry logic with outside tick requirement."""
        current_time = row['time']
        current_tick_type = pd.to_numeric(row.get('tick_type', 0), errors='coerce')
        if pd.isna(current_tick_type):
            current_tick_type = 0
        waiting_state = state['waiting_for_outside_entry']
        buffer_state = state['breakout_buffer_state']
        # 檢查是否有 day_high 突破
        is_breakout = self.entry_checker.check_day_high_breakout(
            state['current_day_high'], state['prev_day_high']
        )
        # *** 重要：先檢查是否處於等待外盤進場狀態 ***
        # 這樣可以避免新突破覆蓋之前的等待狀態
        if waiting_state['active']:
            # 檢查條件：1) 外盤 tick  2) 不同 timestamp
            is_outside_tick = (current_tick_type == 1)
            different_timestamp = (current_time != waiting_state['breakout_time'])
            if is_outside_tick and different_timestamp:
                # 找到符合條件的外盤 tick，繼續檢查進場條件
                logger.info(f"找到外盤 tick at {current_time}，檢查進場條件...")
                # 取消等待狀態，繼續執行進場條件檢查
                waiting_state['active'] = False
                # 注意：即使當前tick也是新突破，也繼續進場檢查（不return）
            else:
                # 不符合條件，繼續等待
                # 但如果當前是新突破，仍要更新等待狀態
                if is_breakout:
                    state['day_high_break_count'] += 1
                    waiting_state['breakout_time'] = current_time
                    waiting_state['breakout_day_high'] = state['current_day_high']
                    waiting_state['prev_day_high'] = state['prev_day_high']
                    logger.info(f"檢測到突破 {state['prev_day_high']:.2f} → {state['current_day_high']:.2f} at {current_time}，等待外盤 tick 進場")
                return
        # 如果不在等待狀態，且檢測到突破
        elif is_breakout:
            state['day_high_break_count'] += 1
            # *** 重要實務考量 ***
            # 突破tick本身是「創造」day_high的成交
            # 在真實交易中，我們不可能同時是：
            #   1) 打出突破的那個人（突破tick的成交者）
            #   2) 看到突破後進場的人
            # 因此，無論突破tick是否為外盤，都必須等待「下一個」外盤tick才能進場
            # 這樣才符合真實交易邏輯：先觀察到突破 → 再反應進場
            # 設置等待外盤進場狀態（無論突破tick是否為外盤）
            waiting_state['active'] = True
            waiting_state['breakout_time'] = current_time
            waiting_state['breakout_day_high'] = state['current_day_high']
            waiting_state['prev_day_high'] = state['prev_day_high']
            logger.info(f"檢測到突破 {state['prev_day_high']:.2f} → {state['current_day_high']:.2f} at {current_time}，等待外盤 tick 進場")
            # 傳統 buffer 機制（如果啟用）
            if self.entry_config.get('entry_buffer_enabled', False):
                buffer_state['active'] = True
                buffer_state['start_time'] = row['time']
                buffer_state['day_high'] = state['current_day_high']
                buffer_state['checked'] = False
            # 突破當下不進場，等待下一個外盤 tick
            return
        fixed_ratio_threshold = state['fixed_ratio_threshold']
        dynamic_ratio_threshold = state['dynamic_ratio_threshold']
        if self.entry_config.get('ratio_increase_after_loss_enabled', False) and self.last_exit_was_stop_loss:
            last_entry_ratio = float(state.get('last_entry_ratio', 0.0) or 0.0)
            default_threshold = float(self.entry_config.get('ratio_entry_threshold', 3.0))
            min_threshold = self.entry_config.get('ratio_increase_min_threshold', None)
            threshold_candidates = []
            if min_threshold is not None:
                threshold_candidates.append(float(min_threshold))
            if last_entry_ratio > 0:
                threshold_candidates.append(last_entry_ratio)
            if threshold_candidates:
                threshold = min(threshold_candidates)
            else:
                threshold = default_threshold
            threshold = max(default_threshold, threshold)
            fixed_ratio_threshold = threshold
            dynamic_ratio_threshold = threshold
        entry_signal = self._check_entry(
            row, stock_id, ref_price, limit_up_price,
            state['prev_day_high'], state['last_bid_ask_ratio'],
            fixed_ratio_threshold, dynamic_ratio_threshold,
            state['first_entry_outside_volume'], is_breakout,
            buffer_state, df, state
        )
        if entry_signal and entry_signal.passed:
            self._execute_entry(entry_signal, state, row)
            if buffer_state.get('active'):
                buffer_state['active'] = False
                buffer_state['start_time'] = None
            self.last_exit_was_stop_loss = False
    def _check_entry(self, row, stock_id: str, ref_price: float, limit_up_price: float,
                    prev_day_high: float, last_bid_ask_ratio: Optional[float],
                    fixed_ratio_threshold: float, dynamic_ratio_threshold: Optional[float],
                    first_entry_outside_volume: Optional[float], is_breakout: bool,
                    buffer_state: Dict, df: pd.DataFrame = None, state: Dict = None) -> Optional[EntrySignal]:
        """Check entry conditions."""
        current_time = row['time']
        buffer_active = False
        if self.entry_config.get('entry_buffer_enabled', False) and buffer_state:
            start_time = buffer_state.get('start_time')
            buffer_ms = self.entry_config.get('entry_buffer_milliseconds', 0)
            if buffer_state.get('active') and start_time and buffer_ms is not None:
                delta_ms = (current_time - start_time).total_seconds() * 1000
                if delta_ms <= buffer_ms:
                    buffer_active = True
                else:
                    buffer_state['active'] = False
                    buffer_state['start_time'] = None
        effective_breakout = is_breakout or buffer_active
        if not effective_breakout:
            return None
        entry_start_time = self.entry_config.get('entry_start_time') if hasattr(self.entry_config, 'get') else None
        if entry_start_time and current_time.time() < entry_start_time:
            return None
        if hasattr(self.entry_config, 'get'):
            cutoff_time = self.entry_config.get('entry_cutoff_time')
            if cutoff_time and current_time.time() >= cutoff_time:
                return None
        indicators = {
            'ratio': row.get('ratio_15s_300s', 0.0),  # 使用正確的 ratio 欄位 (ratio_15s_300s)
            'pct_2min': row.get('pct_2min', 0.0),
            'pct_3min': row.get('pct_3min', 0.0),
            'pct_5min': row.get('pct_5min', 0.0),
            'low_1m': row.get('low_1m', row['price']),  # 1分鐘低點
            'low_3m': row.get('low_3m', row['price']),  # 3分鐘低點
            'low_5m': row.get('low_5m', row['price']),  # 5分鐘低點
            'low_3min': row.get('low_3m', row['price']),  # 保留舊的命名兼容性
            'low_10min': row.get('low_10m', row['price']),
            'low_15min': row.get('low_15m', row['price'])
        }
        # 優先使用當前tick的bid_ask_ratio（如果有效）
        # 如果當前tick沒有五檔資料，則查找同一時間戳的五檔資料
        if isinstance(row, dict):
            current_bid_ask_ratio = row.get('bid_ask_ratio', None)
        else:
            current_bid_ask_ratio = getattr(row, 'bid_ask_ratio', None)
        if current_bid_ask_ratio is not None and pd.notna(current_bid_ask_ratio) and current_bid_ask_ratio > 0:
            ask_bid_ratio = float(current_bid_ask_ratio)
        elif df is not None:
            # 當前tick沒有五檔資料，從DataFrame查找同一時間戳的五檔資料
            same_time_rows = df[df['time'] == current_time]
            valid_ratios = same_time_rows[same_time_rows['bid_ask_ratio'].notna() & (same_time_rows['bid_ask_ratio'] > 0)]
            if len(valid_ratios) > 0:
                ask_bid_ratio = float(valid_ratios.iloc[0]['bid_ask_ratio'])
            else:
                ask_bid_ratio = last_bid_ask_ratio if last_bid_ask_ratio is not None else 0.0
        else:
            ask_bid_ratio = last_bid_ask_ratio if last_bid_ask_ratio is not None else 0.0
        massive_matching_amount = self.massive_matching_tracker.get_massive_matching_amount()
        return self.entry_checker.check_entry_signals(
            stock_id=stock_id,
            current_price=row['price'],
            current_time=current_time,
            prev_day_high=prev_day_high,
            indicators=indicators,
            ask_bid_ratio=ask_bid_ratio,
            ref_price=ref_price,
            massive_matching_amount=massive_matching_amount,
            fixed_ratio_threshold=fixed_ratio_threshold,
            dynamic_ratio_threshold=dynamic_ratio_threshold,
            min_outside_amount=None,
            force_log=True,
            df=df,
            current_row=row,
            is_day_high_breakout=effective_breakout,
            last_exit_time=state.get('last_exit_time') if state else None  # 傳入上次出場時間
        )
    def _execute_entry(self, entry_signal: EntrySignal, state: Dict, row):
        """Execute entry."""
        entry_ratio = row.get('ratio_15s_300s', 0.0)  # 使用正確的 ratio 欄位
        bid_thickness = row.get('bid_avg_volume', 0.0)
        outside_volume = self.outside_volume_tracker.get_current_volume()
        position = self.position_manager.open_position(
            entry_time=entry_signal.time,
            entry_price=entry_signal.price,
            entry_bid_thickness=bid_thickness,
            day_high_at_entry=state['current_day_high'],
            entry_ratio=entry_ratio,
            entry_outside_volume_3s=outside_volume
        )
        position.highest_price = entry_signal.price
        position.allow_reentry = bool(self.reentry_config.get('reentry', False))
        state['last_entry_time'] = entry_signal.time
        state['last_entry_ratio'] = entry_ratio
        state['first_entry_outside_volume'] = outside_volume
        logger.info(
            f"[出場] {entry_signal.time}, {entry_signal.price:.2f}, "
            f"Day High 突破出場"
        )
    def _check_reentry(self, position: Position, row, state: Dict, prev_highest_price: float):
        """Check reentry conditions."""
        current_time = state['current_time']
        current_price = state['current_price']
        if current_price <= prev_highest_price:
            return
        current_outside_volume = self.outside_volume_tracker.get_current_volume()
        if current_outside_volume <= position.entry_outside_volume_3s:
            return
        self._handle_reentry(position, current_time, current_price)
    def _handle_trailing_exit(self, exit_result: Dict):
        """Handle trailing stop exit."""
        self.position_manager.trailing_stop_exit(
            exit_time=exit_result['exit_time'],
            exit_price=exit_result['exit_price'],
            exit_ratio=exit_result.get('exit_ratio', 0.5),
            exit_level=exit_result.get('exit_level', '3min'),
            exit_reason=exit_result.get('exit_reason', '')
        )
    def _handle_exit(self, exit_result: Dict, current_time: datetime, state: Dict = None):
        """Handle exit."""
        if exit_result['exit_type'] == 'partial':
            self.position_manager.partial_exit(
                exit_time=exit_result['exit_time'],
                exit_price=exit_result['exit_price'],
                exit_reason=exit_result['exit_reason']
            )
        else:
            self.position_manager.close_position(
                exit_time=exit_result['exit_time'],
                exit_price=exit_result['exit_price'],
                exit_reason=exit_result['exit_reason']
            )
            if state:
                state['last_exit_time'] = exit_result['exit_time']  # 更新最後出場時間
            self.last_exit_was_stop_loss = exit_result.get('exit_reason') == 'tick_stop_loss'
    def _handle_reentry(self, position: Position, current_time: datetime, current_price: float):
        """Handle reentry."""
        position.reentry_time = current_time
        position.reentry_price = current_price
        position.remaining_ratio = 1.0
        position.allow_reentry = False
        logger.info(f"[??] ??: {current_time}, ?格: {current_price:.2f}")
    def _close_position_completely(self, position: Position, trailing_result: Dict, state: Dict = None):
        """Close position after trailing exits."""
        total_exit_ratio = 0.0
        weighted_price = 0.0
        for exit_item in position.trailing_exits:
            weighted_price += exit_item['price'] * exit_item['ratio']
            total_exit_ratio += exit_item['ratio']
        avg_exit_price = weighted_price / total_exit_ratio if total_exit_ratio > 0 else trailing_result['exit_price']
        self.position_manager.close_position(
            exit_time=trailing_result['exit_time'],
            exit_price=avg_exit_price,
            exit_reason="移動停利全出"
        )
        if state:
            state['last_exit_time'] = trailing_result['exit_time']  # 更新最後出場時間
        self.last_exit_was_stop_loss = False
    def _force_close_at_market_close(self, state: Dict):
        """Force close at market close."""
        if self.position_manager.has_position():
            close_price = state['current_price']
            close_time = state['current_time']
            self.position_manager.close_position(
                exit_time=close_time,
                exit_price=close_price,
                exit_reason="收盤清倉"
            )
            state['last_exit_time'] = close_time  # 更新最後出場時間
            self.last_exit_was_stop_loss = False
            logger.info(f"[收盤清倉] 時間: {close_time}, 價格: {close_price:.2f}")
    def _add_metrics_to_dataframe(self, df: pd.DataFrame, metrics: Dict):
        """Attach metrics to DataFrame."""
        df['day_high_growth_rate'] = metrics['day_high_growth_rates']
        df['bid_avg_volume'] = metrics['bid_avg_volumes']
        df['ask_avg_volume'] = metrics['ask_avg_volumes']
        df['balance_ratio'] = metrics['balance_ratios']
        df['day_high_breakout'] = metrics['day_high_breakouts']
        df['inside_outside_ratio'] = metrics['inside_outside_ratios']
        df['outside_ratio'] = metrics['outside_ratios']
        df['large_order_io_ratio'] = metrics['large_order_io_ratios']
        df['large_order_outside_ratio'] = metrics['large_order_outside_ratios']
