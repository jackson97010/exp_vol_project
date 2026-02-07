"""
持倉管理模組
負責管理持倉狀態和重複進場邏輯
"""

import logging
from datetime import datetime
from typing import Dict, Optional, List
from dataclasses import dataclass, field

logger = logging.getLogger(__name__)


class Position:
    """持倉資訊"""

    def __init__(
        self,
        entry_time: datetime,
        entry_price: float,
        entry_bid_thickness: float,
        day_high_at_entry: float,
        entry_ratio: float,
        entry_outside_volume_3s: float = 0.0  # 新增：進場前3秒外盤金額
    ):
        """
        初始化持倉

        Args:
            entry_time: 進場時間
            entry_price: 進場價格
            entry_bid_thickness: 進場時的買方掛單厚度
            day_high_at_entry: 進場時的Day High
            entry_ratio: 進場時的Ratio
            entry_outside_volume_3s: 進場前3秒的外盤成交金額
        """
        # 基本資訊
        self.entry_time = entry_time
        self.entry_price = entry_price
        self.entry_bid_thickness = entry_bid_thickness
        self.day_high_at_entry = day_high_at_entry
        self.entry_ratio = entry_ratio
        self.entry_outside_volume_3s = entry_outside_volume_3s  # 新增欄位

        # 價格追蹤
        self.highest_price = entry_price

        # 第一階段出場（減碼50%）
        self.partial_exit_done = False
        self.partial_exit_time = None
        self.partial_exit_price = None

        # 回補進場
        self.partial_exit_recovered = False
        self.reentry_time = None
        self.reentry_price = None
        self.reentry_stop_price = None
        self.reentry_outside_amount = 0.0  # 回補後累計外盤金額
        self.reentry_outside_volume_3s = 0.0  # 回補時的3秒外盤金額

        # 最終出場
        self.final_exit_time = None
        self.final_exit_price = None

        # 停損相關
        self.buy_weak_streak = 0  # 掛單弱勢連續計數

        # 其他資訊
        self.share_capital = 0  # 股本（用於動能停損判斷）

        # 移動停利相關欄位
        self.trailing_exits: List[Dict] = []  # 記錄每次移動停利出場
        self.remaining_ratio: float = 1.0     # 剩餘部位比例 (初始為 1.0)
        self.exit_levels_triggered: Dict[str, bool] = {  # 追蹤哪些低點已觸發
            '1min': False,
            '3min': False,
            '5min': False
        }


@dataclass
class TradeRecord:
    """交易記錄"""
    entry_time: datetime
    entry_price: float
    entry_bid_thickness: float
    entry_ratio: float
    day_high_at_entry: float
    entry_outside_volume_3s: float = 0.0

    partial_exit_time: Optional[datetime] = None
    partial_exit_price: Optional[float] = None
    partial_exit_reason: str = ""

    reentry_time: Optional[datetime] = None
    reentry_price: Optional[float] = None
    reentry_stop_price: Optional[float] = None
    reentry_outside_volume_3s: float = 0.0

    final_exit_time: Optional[datetime] = None
    final_exit_price: Optional[float] = None
    final_exit_reason: str = ""
    reentry_exit_reason: Optional[str] = None

    pnl_percent: Optional[float] = None

    # 移動停利相關欄位
    trailing_exit_details: List[Dict] = field(default_factory=list)  # 移動停利出場明細
    total_exits: int = 0                   # 總出場次數
    final_remaining_ratio: float = 1.0     # 最終剩餘部位比例

    def to_dict(self) -> Dict:
        """轉換為字典格式"""
        return {
            'entry_time': self.entry_time,
            'entry_price': self.entry_price,
            'entry_bid_thickness': self.entry_bid_thickness,
            'entry_ratio': self.entry_ratio,
            'day_high_at_entry': self.day_high_at_entry,
            'entry_outside_volume_3s': self.entry_outside_volume_3s,
            'partial_exit_time': self.partial_exit_time,
            'partial_exit_price': self.partial_exit_price,
            'partial_exit_reason': self.partial_exit_reason,
            'reentry_time': self.reentry_time,
            'reentry_price': self.reentry_price,
            'reentry_stop_price': self.reentry_stop_price,
            'reentry_outside_volume_3s': self.reentry_outside_volume_3s,
            'final_exit_time': self.final_exit_time,
            'final_exit_price': self.final_exit_price,
            'final_exit_reason': self.final_exit_reason,
            'reentry_exit_reason': self.reentry_exit_reason,
            'pnl_percent': self.pnl_percent,
            'trailing_exit_details': self.trailing_exit_details,
            'total_exits': self.total_exits,
            'final_remaining_ratio': self.final_remaining_ratio
        }


class PositionManager:
    """持倉管理器"""

    def __init__(self):
        """初始化持倉管理器"""
        self.current_position: Optional[Position] = None
        self.current_trade_record: Optional[TradeRecord] = None
        self.trade_history: List[TradeRecord] = []

    def reset(self):
        """重設持倉管理器狀態"""
        self.current_position = None
        self.current_trade_record = None
        self.trade_history = []

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

    def open_position(
        self,
        entry_time: datetime,
        entry_price: float,
        entry_bid_thickness: float,
        day_high_at_entry: float,
        entry_ratio: float,
        entry_outside_volume_3s: float = 0.0
    ) -> Position:
        """
        開新倉位

        Args:
            entry_time: 進場時間
            entry_price: 進場價格
            entry_bid_thickness: 進場時的買方掛單厚度
            day_high_at_entry: 進場時的Day High
            entry_ratio: 進場時的Ratio
            entry_outside_volume_3s: 進場前3秒的外盤成交金額

        Returns:
            新建立的Position物件
        """
        # 建立新持倉
        self.current_position = Position(
            entry_time=entry_time,
            entry_price=entry_price,
            entry_bid_thickness=entry_bid_thickness,
            day_high_at_entry=day_high_at_entry,
            entry_ratio=entry_ratio,
            entry_outside_volume_3s=entry_outside_volume_3s
        )

        # 建立交易記錄
        self.current_trade_record = TradeRecord(
            entry_time=entry_time,
            entry_price=entry_price,
            entry_bid_thickness=entry_bid_thickness,
            entry_ratio=entry_ratio,
            day_high_at_entry=day_high_at_entry,
            entry_outside_volume_3s=entry_outside_volume_3s
        )

        logger.info(
            f"[開倉] 時間: {entry_time}, 價格: {entry_price:.2f}, "
            f"Day High: {day_high_at_entry:.2f}, Ratio: {entry_ratio:.1f}, "
            f"3秒外盤: {entry_outside_volume_3s/1000000:.2f}M"
        )

        return self.current_position

    def partial_exit(
        self,
        exit_time: datetime,
        exit_price: float,
        exit_reason: str
    ) -> bool:
        """
        部分出場（減碼50%）

        Args:
            exit_time: 出場時間
            exit_price: 出場價格
            exit_reason: 出場原因

        Returns:
            是否成功
        """
        if not self.current_position or self.current_position.partial_exit_done:
            return False

        self.current_position.partial_exit_done = True
        self.current_position.partial_exit_time = exit_time
        self.current_position.partial_exit_price = exit_price

        # 更新剩餘部位（原有邏輯是減碼50%）
        self.current_position.remaining_ratio = 0.5

        if self.current_trade_record:
            self.current_trade_record.partial_exit_time = exit_time
            self.current_trade_record.partial_exit_price = exit_price
            self.current_trade_record.partial_exit_reason = exit_reason

        # 設定回補停損價（減碼價往下1 tick）
        self.current_position.reentry_stop_price = self._add_ticks(exit_price, -1)

        logger.info(
            f"[部分出場] 時間: {exit_time}, 價格: {exit_price:.2f}, "
            f"原因: {exit_reason}, 回補停損價: {self.current_position.reentry_stop_price:.2f}"
        )

        return True

    def reentry_position(
        self,
        reentry_time: datetime,
        reentry_price: float,
        reentry_outside_volume_3s: float
    ) -> bool:
        """
        回補進場

        Args:
            reentry_time: 回補時間
            reentry_price: 回補價格
            reentry_outside_volume_3s: 回補時的3秒外盤金額

        Returns:
            是否成功
        """
        if not self.current_position or not self.current_position.partial_exit_done:
            return False

        if self.current_position.partial_exit_recovered:
            return False

        self.current_position.partial_exit_recovered = True
        self.current_position.reentry_time = reentry_time
        self.current_position.reentry_price = reentry_price
        self.current_position.reentry_outside_amount = 0.0
        self.current_position.reentry_outside_volume_3s = reentry_outside_volume_3s

        # 更新回補停損價（部分出場價往下1 tick）
        if self.current_position.partial_exit_price:
            self.current_position.reentry_stop_price = self._add_ticks(
                self.current_position.partial_exit_price, -1
            )

        if self.current_trade_record:
            self.current_trade_record.reentry_time = reentry_time
            self.current_trade_record.reentry_price = reentry_price
            self.current_trade_record.reentry_stop_price = self.current_position.reentry_stop_price
            self.current_trade_record.reentry_outside_volume_3s = reentry_outside_volume_3s

        logger.info(
            f"[回補進場] 時間: {reentry_time}, 價格: {reentry_price:.2f}, "
            f"3秒外盤: {reentry_outside_volume_3s/1000000:.2f}M, "
            f"停損價: {self.current_position.reentry_stop_price:.2f}"
        )

        return True

    def trailing_stop_exit(
        self,
        exit_time: datetime,
        exit_price: float,
        exit_ratio: float,
        exit_level: str,
        exit_reason: str
    ) -> bool:
        """
        移動停利出場（部分出場）

        Args:
            exit_time: 出場時間
            exit_price: 出場價格
            exit_ratio: 出場比例
            exit_level: 出場級別 ('1min', '3min', '5min')
            exit_reason: 出場原因

        Returns:
            是否成功
        """
        if not self.current_position:
            return False

        # 更新剩餘部位
        self.current_position.remaining_ratio -= exit_ratio

        # 記錄出場
        self.current_position.trailing_exits.append({
            'time': exit_time,
            'price': exit_price,
            'ratio': exit_ratio,
            'level': exit_level,
            'reason': exit_reason
        })

        # 標記該級別已觸發
        if exit_level in self.current_position.exit_levels_triggered:
            self.current_position.exit_levels_triggered[exit_level] = True

        # 更新交易記錄
        if self.current_trade_record:
            self.current_trade_record.trailing_exit_details.append({
                'time': exit_time,
                'price': exit_price,
                'ratio': exit_ratio,
                'level': exit_level,
                'reason': exit_reason
            })
            self.current_trade_record.total_exits += 1
            self.current_trade_record.final_remaining_ratio = self.current_position.remaining_ratio

        logger.info(
            f"[移動停利] 時間: {exit_time}, 價格: {exit_price:.2f}, "
            f"級別: {exit_level}, 出場比例: {exit_ratio:.1%}, "
            f"剩餘部位: {self.current_position.remaining_ratio:.1%}, "
            f"原因: {exit_reason}"
        )

        return True

    def close_position(
        self,
        exit_time: datetime,
        exit_price: float,
        exit_reason: str,
        is_reentry_exit: bool = False
    ) -> Optional[TradeRecord]:
        """
        關閉倉位

        Args:
            exit_time: 出場時間
            exit_price: 出場價格
            exit_reason: 出場原因
            is_reentry_exit: 是否為回補出場

        Returns:
            交易記錄
        """
        if not self.current_position:
            return None

        self.current_position.final_exit_time = exit_time
        self.current_position.final_exit_price = exit_price

        if self.current_trade_record:
            self.current_trade_record.final_exit_time = exit_time
            self.current_trade_record.final_exit_price = exit_price

            if is_reentry_exit:
                self.current_trade_record.reentry_exit_reason = exit_reason
            else:
                self.current_trade_record.final_exit_reason = exit_reason

            # 計算收益率
            entry_price = self.current_trade_record.entry_price

            # 如果有移動停利出場記錄
            if self.current_position.trailing_exits:
                total_pnl = 0.0
                for exit in self.current_position.trailing_exits:
                    exit_pnl = (exit['price'] - entry_price) / entry_price * 100 * exit['ratio']
                    total_pnl += exit_pnl
                # 加上最後剩餘部位的收益
                if self.current_position.remaining_ratio > 0:
                    final_pnl = (exit_price - entry_price) / entry_price * 100 * self.current_position.remaining_ratio
                    total_pnl += final_pnl
                self.current_trade_record.pnl_percent = total_pnl
            elif self.current_trade_record.partial_exit_price:
                # 有部分出場：50% 以部分出場價計算，50% 以最終出場價計算
                profit1 = (self.current_trade_record.partial_exit_price - entry_price) / entry_price * 100 * 0.5
                profit2 = (exit_price - entry_price) / entry_price * 100 * 0.5
                self.current_trade_record.pnl_percent = profit1 + profit2
            else:
                # 全部出場
                self.current_trade_record.pnl_percent = (exit_price - entry_price) / entry_price * 100

            # 加入歷史記錄
            self.trade_history.append(self.current_trade_record)

            logger.info(
                f"[關閉倉位] 時間: {exit_time}, 價格: {exit_price:.2f}, "
                f"原因: {exit_reason}, 收益: {self.current_trade_record.pnl_percent:.2f}%"
            )

        # 重置當前持倉
        trade_record = self.current_trade_record
        self.current_position = None
        self.current_trade_record = None

        return trade_record

    def has_position(self) -> bool:
        """是否有持倉"""
        return (self.current_position is not None
                and self.current_position.remaining_ratio > 0)

    def get_current_position(self) -> Optional[Position]:
        """取得當前持倉"""
        return self.current_position

    def get_trade_history(self) -> List[TradeRecord]:
        """取得交易歷史"""
        return self.trade_history


class ReentryManager:
    """重複進場管理器"""

    def __init__(self, config: Dict):
        """
        初始化重複進場管理器

        Args:
            config: 重複進場設定
        """
        self.config = config

    def check_reentry_conditions(
        self,
        current_row,
        position: Position,
        outside_volume_tracker
    ) -> Dict:
        """
        檢查重複進場條件（新邏輯）

        重複進場條件：
        1. 已出場半倉
        2. 價格創新高（超過歷史最高價）
        3. 過去3秒外盤成交金額 > 進場前3秒的外盤成交金額

        Args:
            current_row: 當前資料列
            position: 持倉物件
            outside_volume_tracker: 外盤金額追蹤器

        Returns:
            檢查結果字典
        """
        result = {
            'pass': False,
            'conditions': {},
            'failure_reason': ''
        }

        current_price = current_row['price']
        current_time = current_row['time']

        # 條件1：已出場半倉
        if not position.partial_exit_done:
            result['conditions']['已出場半倉'] = {
                'pass': False,
                'message': '尚未部分出場'
            }
            result['failure_reason'] = '尚未部分出場'
            return result

        result['conditions']['已出場半倉'] = {
            'pass': True,
            'message': f'已於 {position.partial_exit_time} 部分出場'
        }

        # 條件2：已經回補過則不再回補
        if position.partial_exit_recovered:
            result['conditions']['回補狀態'] = {
                'pass': False,
                'message': '已經回補過'
            }
            result['failure_reason'] = '已經回補過'
            return result

        result['conditions']['回補狀態'] = {
            'pass': True,
            'message': '尚未回補'
        }

        # 條件3：價格創新高（超過歷史最高價）
        if current_price <= position.highest_price:
            result['conditions']['價格創新高'] = {
                'pass': False,
                'value': f'{current_price:.2f}',
                'threshold': f'{position.highest_price:.2f}',
                'message': '未創新高'
            }
            result['failure_reason'] = '價格未創新高'
            return result

        result['conditions']['價格創新高'] = {
            'pass': True,
            'value': f'{current_price:.2f}',
            'threshold': f'{position.highest_price:.2f}',
            'message': '價格創新高'
        }

        # 條件4：過去3秒外盤成交金額 > 進場前3秒的外盤成交金額
        current_outside_volume = outside_volume_tracker.get_volume_3s()
        entry_outside_volume = position.entry_outside_volume_3s

        if current_outside_volume <= entry_outside_volume:
            result['conditions']['外盤金額比較'] = {
                'pass': False,
                'value': f'{current_outside_volume/1000000:.2f}M',
                'threshold': f'{entry_outside_volume/1000000:.2f}M',
                'message': '3秒外盤金額不足'
            }
            result['failure_reason'] = '3秒外盤金額不足'
            logger.debug(
                f"[回補檢查] 時間: {current_time}, 價格: {current_price:.2f}, "
                f"當前3秒外盤: {current_outside_volume/1000000:.2f}M, "
                f"進場時3秒外盤: {entry_outside_volume/1000000:.2f}M - 不足"
            )
            return result

        result['conditions']['外盤金額比較'] = {
            'pass': True,
            'value': f'{current_outside_volume/1000000:.2f}M',
            'threshold': f'{entry_outside_volume/1000000:.2f}M',
            'message': '3秒外盤金額充足'
        }

        # 所有條件通過
        result['pass'] = True
        result['failure_reason'] = ''

        logger.info(
            f"[回補條件滿足] 時間: {current_time}, 價格: {current_price:.2f} (新高), "
            f"當前3秒外盤: {current_outside_volume/1000000:.2f}M > "
            f"進場時3秒外盤: {entry_outside_volume/1000000:.2f}M"
        )

        return result

    def check_reentry_conditions_with_volume(
        self,
        current_row,
        position: Position,
        override_volume: float
    ) -> Dict:
        """
        檢查重複進場條件（使用指定的外盤金額）
        用於 buffer 機制，可以指定要使用的外盤金額值

        Args:
            current_row: 當前資料列
            position: 持倉物件
            override_volume: 要使用的外盤金額（例如 buffer 期間的最大值）

        Returns:
            檢查結果字典
        """
        result = {
            'pass': False,
            'conditions': {},
            'failure_reason': ''
        }

        current_price = current_row['price']
        current_time = current_row['time']

        # 條件1：已出場半倉
        if not position.partial_exit_done:
            result['conditions']['已出場半倉'] = {
                'pass': False,
                'message': '尚未部分出場'
            }
            result['failure_reason'] = '尚未部分出場'
            return result

        result['conditions']['已出場半倉'] = {
            'pass': True,
            'message': f'已於 {position.partial_exit_time} 部分出場'
        }

        # 條件2：已經回補過則不再回補
        if position.partial_exit_recovered:
            result['conditions']['回補狀態'] = {
                'pass': False,
                'message': '已經回補過'
            }
            result['failure_reason'] = '已經回補過'
            return result

        result['conditions']['回補狀態'] = {
            'pass': True,
            'message': '尚未回補'
        }

        # 條件3：價格創新高（超過歷史最高價）
        if current_price <= position.highest_price:
            result['conditions']['價格創新高'] = {
                'pass': False,
                'value': f'{current_price:.2f}',
                'threshold': f'{position.highest_price:.2f}',
                'message': '未創新高'
            }
            result['failure_reason'] = '價格未創新高'
            return result

        result['conditions']['價格創新高'] = {
            'pass': True,
            'value': f'{current_price:.2f}',
            'threshold': f'{position.highest_price:.2f}',
            'message': '價格創新高'
        }

        # 條件4：使用指定的外盤金額進行比較
        current_outside_volume = override_volume  # 使用指定的值
        entry_outside_volume = position.entry_outside_volume_3s

        if current_outside_volume <= entry_outside_volume:
            result['conditions']['外盤金額比較'] = {
                'pass': False,
                'value': f'{current_outside_volume/1000000:.2f}M',
                'threshold': f'{entry_outside_volume/1000000:.2f}M',
                'message': '3秒外盤金額不足'
            }
            result['failure_reason'] = '3秒外盤金額不足'
            return result

        result['conditions']['外盤金額比較'] = {
            'pass': True,
            'value': f'{current_outside_volume/1000000:.2f}M',
            'threshold': f'{entry_outside_volume/1000000:.2f}M',
            'message': '3秒外盤金額充足 (使用buffer最大值)'
        }

        # 所有條件通過
        result['pass'] = True
        result['failure_reason'] = ''

        logger.info(
            f"[回補條件滿足-Buffer] 時間: {current_time}, 價格: {current_price:.2f} (新高), "
            f"Buffer期間最大外盤: {current_outside_volume/1000000:.2f}M > "
            f"進場時3秒外盤: {entry_outside_volume/1000000:.2f}M"
        )

        return result