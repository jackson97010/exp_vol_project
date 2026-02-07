"""
策略設定載入模組
負責載入和管理策略設定
"""

import os
import yaml
import logging
import pandas as pd
from datetime import datetime, time
from typing import Dict, Optional

logger = logging.getLogger(__name__)


class StrategyConfig:
    """策略設定管理器"""

    def __init__(self, config_path: str = "Bo_v2.yaml"):
        """
        初始化策略設定

        Args:
            config_path: 設定檔路徑
        """
        self.config_path = config_path
        self._config = self._load_config()

    def _load_config(self) -> Dict:
        """載入設定檔"""
        default_config = self._get_default_config()

        if not os.path.exists(self.config_path):
            logger.warning(f"設定檔不存在: {self.config_path}，使用預設值")
            return default_config

        try:
            with open(self.config_path, 'r', encoding='utf-8') as f:
                cfg = yaml.safe_load(f) or {}
        except Exception as exc:
            logger.warning(f"無法載入設定檔: {exc}，使用預設值")
            return default_config

        # 合併預設值和載入的設定
        strategy = cfg.get('strategy', {})

        # 更新設定值
        config = default_config.copy()

        # 時間設定
        config['entry_start_time'] = self._parse_time(
            strategy.get('entry_start_time', '09:09:00'),
            default_config['entry_start_time']
        )
        config['entry_cutoff_time'] = self._parse_time(
            strategy.get('entry_cutoff_time', '13:00:00'),
            default_config['entry_cutoff_time']
        )

        # 進場條件設定
        config['entry_cooldown'] = float(strategy.get('entry_cooldown', 30.0))
        config['ask_bid_ratio_threshold'] = float(strategy.get('ask_bid_ratio_threshold', 1.0))
        config['massive_matching_enabled'] = bool(strategy.get('massive_matching_enabled', True))
        config['massive_matching_amount'] = float(strategy.get('massive_matching_amount', 1000000.0))

        # 動態流動性門檻設定
        config['use_dynamic_liquidity_threshold'] = bool(strategy.get('use_dynamic_liquidity_threshold', False))
        config['dynamic_liquidity_threshold_file'] = str(strategy.get('dynamic_liquidity_threshold_file', 'daily_liquidity_threshold.parquet'))
        config['dynamic_liquidity_multiplier'] = float(strategy.get('dynamic_liquidity_multiplier', 0.004))  # 預設 0.4%
        config['dynamic_liquidity_threshold_cap'] = float(strategy.get('dynamic_liquidity_threshold_cap', 50_000_000))  # 上限 5000 萬
        config['dynamic_liquidity_threshold_df'] = None  # 初始化為 None，稍後載入

        config['ratio_entry_enabled'] = bool(strategy.get('ratio_entry_enabled', True))
        config['ratio_entry_threshold'] = float(strategy.get('ratio_entry_threshold', 3.0))
        config['ratio_column'] = str(strategy.get('ratio_column', 'ratio_15s_300s'))

        # *** 重要修改：漲幅限制從 7.4% 改為 8.5% ***
        config['price_change_limit_enabled'] = bool(strategy.get('price_change_limit_enabled', True))
        config['price_change_limit_pct'] = float(strategy.get('price_change_limit_pct', 8.5))  # 預設改為 8.5%

        # 區間漲幅濾網
        config['interval_pct_filter_enabled'] = bool(strategy.get('interval_pct_filter_enabled', False))
        minutes = int(strategy.get('interval_pct_minutes', 3))
        config['interval_pct_minutes'] = minutes if minutes in (2, 3, 5) else 3
        config['interval_pct_threshold'] = float(strategy.get('interval_pct_threshold', 4.0))

        # 進場 Buffer 機制
        config['entry_buffer_enabled'] = bool(strategy.get('entry_buffer_enabled', False))
        config['entry_buffer_milliseconds'] = int(strategy.get('entry_buffer_milliseconds', 20))

        # 平盤檢查設定
        config['above_open_check_enabled'] = bool(strategy.get('above_open_check_enabled', True))

        # 10分鐘低點檢查
        config['low_10min_filter_enabled'] = bool(strategy.get('low_10min_filter_enabled', False))
        config['low_10min_threshold'] = float(strategy.get('low_10min_threshold', 4.0))

        # 突破品質檢查設定（新策略）
        config['breakout_quality_check_enabled'] = bool(strategy.get('breakout_quality_check_enabled', False))  # 預設改為 False
        config['breakout_min_volume'] = int(strategy.get('breakout_min_volume', 10))
        config['breakout_min_ask_eat_ratio'] = float(strategy.get('breakout_min_ask_eat_ratio', 0.25))
        config['breakout_absolute_large_volume'] = int(strategy.get('breakout_absolute_large_volume', 50))

        # 小單推升過濾設定（舊策略）
        config['small_order_filter_enabled'] = bool(strategy.get('small_order_filter_enabled', False))  # 預設改為False
        config['small_order_check_trades'] = int(strategy.get('small_order_check_trades', 30))
        config['small_order_threshold'] = int(strategy.get('small_order_threshold', 3))
        config['tiny_order_threshold'] = int(strategy.get('tiny_order_threshold', 2))
        config['single_order_threshold'] = int(strategy.get('single_order_threshold', 1))
        config['small_order_ratio_limit'] = float(strategy.get('small_order_ratio_limit', 0.8))
        config['tiny_order_ratio_limit'] = float(strategy.get('tiny_order_ratio_limit', 0.5))
        config['single_order_ratio_limit'] = float(strategy.get('single_order_ratio_limit', 0.4))
        config['require_large_order_confirmation'] = bool(strategy.get('require_large_order_confirmation', False))
        config['large_order_threshold'] = int(strategy.get('large_order_threshold', 20))
        config['min_large_orders'] = int(strategy.get('min_large_orders', 2))

        # 重複進場設定
        config['reentry'] = bool(strategy.get('reentry', False))
        config['time_settings'] = str(strategy.get('time_settings', '1s'))

        # 輸出路徑設定
        config['output_path'] = str(strategy.get('output_path', 'D:/回測結果'))

        # Ratio 門檻遞增設定
        config['ratio_increase_after_loss_enabled'] = bool(strategy.get('ratio_increase_after_loss_enabled', False))
        config['ratio_increase_multiplier'] = float(strategy.get('ratio_increase_multiplier', 1.0))
        config['ratio_increase_min_threshold'] = float(strategy.get('ratio_increase_min_threshold', 6.0))

        # 停損/停利參數
        config['strategy_b_stop_loss_ticks_small'] = int(strategy.get('strategy_b_stop_loss_ticks_small', 4))
        config['strategy_b_stop_loss_ticks_large'] = int(strategy.get('strategy_b_stop_loss_ticks_large', 3))
        config['momentum_stop_loss_seconds'] = float(strategy.get('momentum_stop_loss_seconds', 15.0))
        config['momentum_stop_loss_seconds_extended'] = float(strategy.get('momentum_stop_loss_seconds_extended', 30.0))
        config['momentum_stop_loss_amount'] = float(strategy.get('momentum_stop_loss_amount', 5000000.0))
        config['momentum_extended_capital_threshold'] = float(strategy.get('momentum_extended_capital_threshold', 50000000000))
        config['momentum_extended_price_min'] = float(strategy.get('momentum_extended_price_min', 100.0))
        config['momentum_extended_price_max'] = float(strategy.get('momentum_extended_price_max', 500.0))

        # 移動停利設定
        config['trailing_stop'] = strategy.get('trailing_stop', {
            'enabled': False,
            'levels': [],
            'entry_price_protection': True
        })

        # 載入動態流動性門檻資料（如果啟用）
        if config['use_dynamic_liquidity_threshold']:
            try:
                threshold_file = config['dynamic_liquidity_threshold_file']
                # 如果是相對路徑，則相對於config檔案所在目錄
                if not os.path.isabs(threshold_file):
                    config_dir = os.path.dirname(os.path.abspath(self.config_path))
                    threshold_file = os.path.join(config_dir, threshold_file)

                if os.path.exists(threshold_file):
                    config['dynamic_liquidity_threshold_df'] = pd.read_parquet(threshold_file)
                    logger.info(f"✓ 已載入動態流動性門檻檔案: {threshold_file}")
                    logger.info(f"  資料範圍: {config['dynamic_liquidity_threshold_df'].index.min()} ~ {config['dynamic_liquidity_threshold_df'].index.max()}")
                    logger.info(f"  包含股票數: {len(config['dynamic_liquidity_threshold_df'].columns)}")
                else:
                    logger.warning(f"⚠ 動態流動性門檻檔案不存在: {threshold_file}")
                    logger.warning(f"  將使用固定門檻 massive_matching_amount")
                    config['use_dynamic_liquidity_threshold'] = False
            except Exception as e:
                logger.error(f"❌ 載入動態流動性門檻檔案失敗: {e}")
                logger.warning(f"  將使用固定門檻 massive_matching_amount")
                config['use_dynamic_liquidity_threshold'] = False
                config['dynamic_liquidity_threshold_df'] = None

        return config

    def _get_default_config(self) -> Dict:
        """取得預設設定"""
        return {
            # 時間設定
            'entry_start_time': time(9, 9, 0),
            'entry_cutoff_time': time(13, 0, 0),
            'entry_cooldown': 30.0,

            # 進場條件
            'ask_bid_ratio_threshold': 1.0,
            'massive_matching_enabled': True,
            'massive_matching_amount': 1000000.0,
            'use_dynamic_liquidity_threshold': False,
            'dynamic_liquidity_threshold_file': 'daily_liquidity_threshold.parquet',
            'dynamic_liquidity_multiplier': 0.004,  # 預設 0.4%
            'dynamic_liquidity_threshold_cap': 50_000_000,  # 上限 5000 萬
            'dynamic_liquidity_threshold_df': None,
            'ratio_entry_enabled': True,
            'ratio_entry_threshold': 3.0,
            'ratio_column': 'ratio_15s_300s',

            # *** 漲幅限制改為 8.5% ***
            'price_change_limit_enabled': True,
            'price_change_limit_pct': 8.5,  # 從 7.4% 改為 8.5%

            # 區間漲幅濾網
            'interval_pct_filter_enabled': False,
            'interval_pct_minutes': 3,
            'interval_pct_threshold': 4.0,

            # 進場 Buffer
            'entry_buffer_enabled': False,
            'entry_buffer_milliseconds': 20,

            # 10分鐘低點檢查
            'low_10min_filter_enabled': False,
            'low_10min_threshold': 4.0,

            # 重複進場
            'reentry': False,
            'time_settings': '1s',

            # 輸出路徑
            'output_path': 'D:/回測結果',

            # Ratio 門檻遞增
            'ratio_increase_after_loss_enabled': False,
            'ratio_increase_multiplier': 1.0,
            'ratio_increase_min_threshold': 6.0,

            # 停損/停利參數
            'strategy_b_stop_loss_ticks_small': 4,
            'strategy_b_stop_loss_ticks_large': 3,
            'momentum_stop_loss_seconds': 15.0,
            'momentum_stop_loss_seconds_extended': 30.0,
            'momentum_stop_loss_amount': 5000000.0,
            'momentum_extended_capital_threshold': 50000000000,
            'momentum_extended_price_min': 100.0,
            'momentum_extended_price_max': 500.0
        }

    def _parse_time(self, value: str, fallback: time) -> time:
        """解析時間字串"""
        try:
            return datetime.strptime(str(value), '%H:%M:%S').time()
        except Exception:
            return fallback

    def get_entry_config(self) -> Dict:
        """取得進場相關設定"""
        return {
            'entry_start_time': self._config['entry_start_time'],
            'entry_cutoff_time': self._config['entry_cutoff_time'],
            'entry_cooldown': self._config['entry_cooldown'],
            'above_open_check_enabled': self._config.get('above_open_check_enabled', True),  # 新增平盤檢查設定
            'ask_bid_ratio_threshold': self._config['ask_bid_ratio_threshold'],
            'massive_matching_enabled': self._config['massive_matching_enabled'],
            'massive_matching_amount': self._config['massive_matching_amount'],
            'use_dynamic_liquidity_threshold': self._config['use_dynamic_liquidity_threshold'],
            'dynamic_liquidity_threshold_file': self._config['dynamic_liquidity_threshold_file'],
            'dynamic_liquidity_multiplier': self._config['dynamic_liquidity_multiplier'],
            'dynamic_liquidity_threshold_cap': self._config['dynamic_liquidity_threshold_cap'],
            'dynamic_liquidity_threshold_df': self._config['dynamic_liquidity_threshold_df'],
            'ratio_entry_enabled': self._config['ratio_entry_enabled'],
            'ratio_entry_threshold': self._config['ratio_entry_threshold'],
            'ratio_column': self._config['ratio_column'],
            'price_change_limit_enabled': self._config['price_change_limit_enabled'],
            'price_change_limit_pct': self._config['price_change_limit_pct'],
            'interval_pct_filter_enabled': self._config['interval_pct_filter_enabled'],
            'interval_pct_minutes': self._config['interval_pct_minutes'],
            'interval_pct_threshold': self._config['interval_pct_threshold'],
            'entry_buffer_enabled': self._config['entry_buffer_enabled'],
            'entry_buffer_milliseconds': self._config['entry_buffer_milliseconds'],
            'low_10min_filter_enabled': self._config.get('low_10min_filter_enabled', False),
            'low_10min_threshold': self._config.get('low_10min_threshold', 4.0),
            # 突破品質檢查設定
            'breakout_quality_check_enabled': self._config.get('breakout_quality_check_enabled', False),
            'breakout_min_volume': self._config.get('breakout_min_volume', 10),
            'breakout_min_ask_eat_ratio': self._config.get('breakout_min_ask_eat_ratio', 0.25),
            'breakout_absolute_large_volume': self._config.get('breakout_absolute_large_volume', 50),
            # 小單過濾設定
            'small_order_filter_enabled': self._config.get('small_order_filter_enabled', False),
            'small_order_check_trades': self._config.get('small_order_check_trades', 30),
            'small_order_threshold': self._config.get('small_order_threshold', 3),
            'tiny_order_threshold': self._config.get('tiny_order_threshold', 2),
            'single_order_threshold': self._config.get('single_order_threshold', 1),
            'small_order_ratio_limit': self._config.get('small_order_ratio_limit', 0.8),
            'tiny_order_ratio_limit': self._config.get('tiny_order_ratio_limit', 0.5),
            'single_order_ratio_limit': self._config.get('single_order_ratio_limit', 0.4),
            'require_large_order_confirmation': self._config.get('require_large_order_confirmation', False),
            'large_order_threshold': self._config.get('large_order_threshold', 20),
            'min_large_orders': self._config.get('min_large_orders', 2)
        }

    def get_exit_config(self) -> Dict:
        """取得出場相關設定"""
        return {
            'strategy_b_stop_loss_ticks_small': self._config['strategy_b_stop_loss_ticks_small'],
            'strategy_b_stop_loss_ticks_large': self._config['strategy_b_stop_loss_ticks_large'],
            'momentum_stop_loss_seconds': self._config['momentum_stop_loss_seconds'],
            'momentum_stop_loss_seconds_extended': self._config['momentum_stop_loss_seconds_extended'],
            'momentum_stop_loss_amount': self._config['momentum_stop_loss_amount'],
            'momentum_extended_capital_threshold': self._config['momentum_extended_capital_threshold'],
            'momentum_extended_price_min': self._config['momentum_extended_price_min'],
            'momentum_extended_price_max': self._config['momentum_extended_price_max'],
            # 移動停利配置
            'trailing_stop': self._config.get('trailing_stop', {})
        }

    def get_reentry_config(self) -> Dict:
        """取得重複進場相關設定"""
        return {
            'reentry': self._config['reentry'],
            'time_settings': self._config['time_settings'],
            'ratio_increase_after_loss_enabled': self._config['ratio_increase_after_loss_enabled'],
            'ratio_increase_multiplier': self._config['ratio_increase_multiplier'],
            'ratio_increase_min_threshold': self._config['ratio_increase_min_threshold']
        }

    def get_all_config(self) -> Dict:
        """取得所有設定"""
        return self._config.copy()