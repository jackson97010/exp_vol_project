"""
資料處理模組
負責載入和處理回測資料
"""

import os
import logging
import pandas as pd
import numpy as np
from datetime import datetime
from typing import Dict, Tuple, Optional
from decimal import Decimal

logger = logging.getLogger(__name__)


class DataProcessor:
    """資料處理器"""

    def __init__(self, config: Dict):
        """
        初始化資料處理器

        Args:
            config: 設定字典
        """
        self.config = config
        self.company_info_cache = None  # 快取公司資訊

    def load_feature_data(self, stock_id: str, date: str) -> pd.DataFrame:
        """
        載入特徵資料

        Args:
            stock_id: 股票代碼
            date: 日期 (YYYY-MM-DD)

        Returns:
            特徵資料 DataFrame
        """
        feature_path = f'D:/feature_data/feature/{date}/{stock_id}.parquet'

        if not os.path.exists(feature_path):
            logger.error(f"檔案不存在: {feature_path}")
            return pd.DataFrame()

        logger.info(f"載入資料: {feature_path}")
        df = pd.read_parquet(feature_path)

        # 轉換時間欄位
        if 'time' in df.columns:
            df['time'] = pd.to_datetime(df['time'])
        else:
            logger.error("資料中沒有 'time' 欄位")
            return pd.DataFrame()

        # 篩選交易時段 (09:00-13:30)
        df = df[
            (df['time'].dt.time >= pd.Timestamp('09:00:00').time()) &
            (df['time'].dt.time <= pd.Timestamp('13:30:00').time())
        ]

        logger.info(f"載入 {len(df)} 筆資料")
        return df

    def process_trade_data(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        處理交易資料

        Args:
            df: 原始資料 DataFrame

        Returns:
            處理後的交易資料 DataFrame
        """
        if 'type' not in df.columns:
            logger.warning("資料中沒有 'type' 欄位，假設全部為 Trade 資料")
            return df

        # 分離 Trade 和 Depth 資料
        trade_df = df[df['type'] == 'Trade'].copy()
        depth_df = df[df['type'] == 'Depth'].copy()

        logger.info(f"Trade 資料筆數: {len(trade_df)}")
        logger.info(f"Depth 資料筆數: {len(depth_df)}")

        if len(trade_df) == 0:
            logger.warning("沒有 Trade 資料")
            return pd.DataFrame()

        # 將 Trade 資料中的掛單欄位值從 0 改為 NaN（這樣才能被 ffill 填充）
        order_book_columns = [
            'bid1_volume', 'bid2_volume', 'bid3_volume', 'bid4_volume', 'bid5_volume',
            'ask1_volume', 'ask2_volume', 'ask3_volume', 'ask4_volume', 'ask5_volume',
            'bid_volume_5level', 'ask_volume_5level', 'bid_ask_ratio'
        ]

        for col in order_book_columns:
            if col in trade_df.columns:
                trade_df[col] = trade_df[col].replace(0, np.nan)

        # 合併資料，保留所有欄位
        merged_df = pd.concat([trade_df, depth_df]).sort_values('time')

        # 向前填充掛單資料欄位
        for col in order_book_columns:
            if col in merged_df.columns:
                merged_df[col] = merged_df[col].ffill()

        # 只保留 Trade 類型的資料進行交易邏輯判斷
        result_df = merged_df[merged_df['type'] == 'Trade'].copy()

        # 預先補齊 5 檔合計，避免逐筆計算成本
        bid_cols = [f"bid{i}_volume" for i in range(1, 6)]
        ask_cols = [f"ask{i}_volume" for i in range(1, 6)]

        if all(col in result_df.columns for col in bid_cols):
            if 'bid_volume_5level' not in result_df.columns:
                result_df['bid_volume_5level'] = result_df[bid_cols].sum(axis=1, min_count=1)
            else:
                mask = result_df['bid_volume_5level'].isna() | (result_df['bid_volume_5level'] == 0)
                if mask.any():
                    result_df.loc[mask, 'bid_volume_5level'] = result_df.loc[mask, bid_cols].sum(axis=1, min_count=1)

        if all(col in result_df.columns for col in ask_cols):
            if 'ask_volume_5level' not in result_df.columns:
                result_df['ask_volume_5level'] = result_df[ask_cols].sum(axis=1, min_count=1)
            else:
                mask = result_df['ask_volume_5level'].isna() | (result_df['ask_volume_5level'] == 0)
                if mask.any():
                    result_df.loc[mask, 'ask_volume_5level'] = result_df.loc[mask, ask_cols].sum(axis=1, min_count=1)

        logger.info(f"處理後 Trade 資料筆數: {len(result_df)}")
        return result_df

    def merge_depth_data(self, trade_df: pd.DataFrame, depth_df: pd.DataFrame) -> pd.DataFrame:
        """
        合併深度資料到交易資料

        Args:
            trade_df: 交易資料 DataFrame
            depth_df: 深度資料 DataFrame

        Returns:
            合併後的 DataFrame
        """
        order_book_columns = [
            'bid1_volume', 'bid2_volume', 'bid3_volume', 'bid4_volume', 'bid5_volume',
            'ask1_volume', 'ask2_volume', 'ask3_volume', 'ask4_volume', 'ask5_volume',
            'bid_volume_5level', 'ask_volume_5level', 'bid_ask_ratio'
        ]

        # 將 Trade 資料中的掛單欄位值從 0 改為 NaN
        for col in order_book_columns:
            if col in trade_df.columns:
                trade_df[col] = trade_df[col].replace(0, np.nan)

        # 合併資料
        merged_df = pd.concat([trade_df, depth_df]).sort_values('time')

        # 向前填充掛單資料
        for col in order_book_columns:
            if col in merged_df.columns:
                merged_df[col] = merged_df[col].ffill()

        # 只保留 Trade 類型的資料
        result_df = merged_df[merged_df['type'] == 'Trade'].copy()

        # 預先補齊 5 檔合計，避免逐筆計算成本
        bid_cols = [f"bid{i}_volume" for i in range(1, 6)]
        ask_cols = [f"ask{i}_volume" for i in range(1, 6)]

        if all(col in result_df.columns for col in bid_cols):
            if 'bid_volume_5level' not in result_df.columns:
                result_df['bid_volume_5level'] = result_df[bid_cols].sum(axis=1, min_count=1)
            else:
                mask = result_df['bid_volume_5level'].isna() | (result_df['bid_volume_5level'] == 0)
                if mask.any():
                    result_df.loc[mask, 'bid_volume_5level'] = result_df.loc[mask, bid_cols].sum(axis=1, min_count=1)

        if all(col in result_df.columns for col in ask_cols):
            if 'ask_volume_5level' not in result_df.columns:
                result_df['ask_volume_5level'] = result_df[ask_cols].sum(axis=1, min_count=1)
            else:
                mask = result_df['ask_volume_5level'].isna() | (result_df['ask_volume_5level'] == 0)
                if mask.any():
                    result_df.loc[mask, 'ask_volume_5level'] = result_df.loc[mask, ask_cols].sum(axis=1, min_count=1)

        return result_df

    def get_reference_price(self, stock_id: str, date: str, close_path: str = None) -> float:
        """
        取得參考價格（昨收價）

        Args:
            stock_id: 股票代碼
            date: 日期 (YYYY-MM-DD)
            close_path: 收盤價檔案路徑

        Returns:
            參考價格
        """
        if close_path is None:
            close_path = r'C:\Users\User\Documents\_02_bt\Backtest_tick_module\close.parquet'

        ref_price = None

        if os.path.exists(close_path):
            try:
                close_df = pd.read_parquet(close_path)

                if stock_id in close_df.columns:
                    # 將日期格式轉換
                    close_df.index = pd.to_datetime(close_df.index)
                    current_date = pd.to_datetime(date)

                    # 找到小於當前日期的最後一個交易日
                    prev_dates = close_df.index[close_df.index < current_date]
                    if len(prev_dates) > 0:
                        prev_date = prev_dates[-1]
                        ref_price = close_df.loc[prev_date, stock_id]
                        logger.info(f"從 close.parquet 讀取到昨收價: {ref_price:.2f} (日期: {prev_date.date()})")
                    else:
                        logger.warning(f"在 close.parquet 中找不到 {date} 之前的收盤價")
                else:
                    logger.warning(f"在 close.parquet 中找不到股票 {stock_id}")
            except Exception as e:
                logger.error(f"讀取 close.parquet 失敗: {e}")
        else:
            logger.warning(f"找不到 close.parquet 檔案: {close_path}")

        return ref_price

    @staticmethod
    def get_tick_size_decimal(price: float) -> Decimal:
        """
        根據價格回傳對應的升降單位

        Args:
            price: 價格

        Returns:
            升降單位（Decimal）
        """
        if pd.isna(price):
            return Decimal('0')

        p = float(price)

        tick_rules = [
            (1000, 5), (500, 1), (100, 0.5), (50, 0.1), (10, 0.05)
        ]

        for threshold, tick in tick_rules:
            if p >= threshold:
                return Decimal(str(tick))

        return Decimal('0.01')

    @staticmethod
    def calculate_limit_up(previous_close: float) -> float:
        """
        計算漲停板：(昨收 * 1.1) 根據該價位 Tick 向下取整

        Args:
            previous_close: 昨收價

        Returns:
            漲停價
        """
        if pd.isna(previous_close):
            return 0.0

        raw_limit = Decimal(str(previous_close)) * Decimal('1.10')
        tick = DataProcessor.get_tick_size_decimal(float(raw_limit))
        limit_up = (raw_limit // tick) * tick

        return round(float(limit_up), 2)

    @staticmethod
    def calculate_limit_down(previous_close: float) -> float:
        """
        計算跌停板：(昨收 * 0.9) 根據該價位 Tick 向上取整

        Args:
            previous_close: 昨收價

        Returns:
            跌停價
        """
        if pd.isna(previous_close):
            return 0.0

        raw_limit = Decimal(str(previous_close)) * Decimal('0.90')
        tick = DataProcessor.get_tick_size_decimal(float(raw_limit))
        # 向上取整
        limit_down = (((raw_limit + tick - Decimal('0.01')) // tick) * tick).normalize()

        return float(limit_down)

    def add_calculated_columns(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        添加計算欄位

        Args:
            df: 原始資料 DataFrame

        Returns:
            添加計算欄位後的 DataFrame
        """
        # 添加空欄位，稍後會在主程式中填充
        df['day_high_growth_rate'] = 0.0
        df['bid_avg_volume'] = 0.0
        df['ask_avg_volume'] = 0.0
        df['balance_ratio'] = 0.0
        df['day_high_breakout'] = False

        return df

    def get_company_name(self, stock_id: str) -> str:
        """
        取得公司名稱

        Args:
            stock_id: 股票代碼

        Returns:
            公司名稱，如果找不到則返回股票代碼
        """
        try:
            # 如果尚未載入公司資訊，嘗試載入
            if self.company_info_cache is None:
                # 嘗試從不同路徑載入公司資訊
                possible_paths = [
                    'company_basic_info.parquet',
                    'D:/feature_data/company_basic_info.parquet',
                    'C:/Users/User/Documents/_02_bt/Backtest_tick_module/company_basic_info.parquet'
                ]

                for path in possible_paths:
                    if os.path.exists(path):
                        self.company_info_cache = pd.read_parquet(path)
                        logger.info(f"載入公司資訊從: {path}")
                        break

                # 如果有成功載入，建立名稱對應字典
                if self.company_info_cache is not None:
                    if '公司簡稱' in self.company_info_cache.columns and 'stock_id' in self.company_info_cache.columns:
                        self.name_map = self.company_info_cache.set_index('stock_id')['公司簡稱'].to_dict()
                    else:
                        logger.warning("公司資訊檔案格式不正確")
                        self.name_map = {}
                else:
                    logger.warning("找不到公司資訊檔案")
                    self.name_map = {}

            # 返回公司名稱
            return self.name_map.get(stock_id, stock_id)

        except Exception as e:
            logger.warning(f"載入公司名稱失敗: {e}")
            return stock_id

    def filter_trading_hours(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        篩選交易時段資料

        Args:
            df: 原始資料 DataFrame

        Returns:
            篩選後的 DataFrame
        """
        # 篩選交易時段 (09:00-13:30)
        trading_df = df[
            (df['time'].dt.time >= pd.Timestamp('09:00:00').time()) &
            (df['time'].dt.time <= pd.Timestamp('13:30:00').time())
        ].copy()

        return trading_df

    def validate_data(self, df: pd.DataFrame) -> bool:
        """
        驗證資料完整性

        Args:
            df: 資料 DataFrame

        Returns:
            是否通過驗證
        """
        # 檢查必要欄位
        required_columns = ['time', 'price', 'volume', 'tick_type']
        missing_columns = [col for col in required_columns if col not in df.columns]

        if missing_columns:
            logger.error(f"缺少必要欄位: {missing_columns}")
            return False

        # 檢查資料數量
        if len(df) == 0:
            logger.error("資料為空")
            return False

        # 檢查時間順序
        if not df['time'].is_monotonic_increasing:
            logger.warning("資料時間順序不正確，將進行排序")
            df.sort_values('time', inplace=True)

        return True
