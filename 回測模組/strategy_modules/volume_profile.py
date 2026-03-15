"""Volume Profile 預測器模組

實作 CMRI 論文《An Adaptive Order Execution Strategy for VWAP Tracking》
中的公式 (1)(2)(3)，利用線性迴歸動態預測剩餘全日量，並據此計算
每分鐘的子委託量 (child order volume)。

迴歸模型：
    V_remain = beta1 * V_ATO + beta2 * V_current + beta3 * elapsed_ratio + intercept

其中：
    - V_ATO:           開盤集合競價量 (09:00~09:05)
    - V_current:       截至目前的累積市場量
    - elapsed_ratio:   已過交易時間占比 (0~1)
    - V_remain:        預測從現在到收盤的剩餘量

子委託量公式 (3)：
    vi = clip( (delta_market / (V_hat - V_prev)) * Q_remaining, 0, Q_remaining )
"""

from __future__ import annotations

import logging
import os
from typing import List, Optional, Tuple

import numpy as np
import pandas as pd

logger = logging.getLogger(__name__)

# 台股交易時段總分鐘數 (09:00 ~ 13:30 = 270 分鐘)
_TOTAL_TRADING_MINUTES: int = 270


class VolumeProfilePredictor:
    """Volume Profile 迴歸預測器。

    使用歷史資料訓練線性迴歸模型，預測當日剩餘成交量。

    Attributes:
        beta1: V_ATO 係數。
        beta2: V_current 係數。
        beta3: elapsed_ratio 係數。
        intercept: 截距項。
        r_squared: 訓練集 R-squared。
    """

    def __init__(
        self,
        beta1: float,
        beta2: float,
        beta3: float,
        intercept: float,
        r_squared: float = 0.0,
    ) -> None:
        self.beta1 = beta1
        self.beta2 = beta2
        self.beta3 = beta3
        self.intercept = intercept
        self.r_squared = r_squared

        logger.info(
            "VolumeProfilePredictor: beta1=%.4f, beta2=%.4f, "
            "beta3=%.4f, intercept=%.4f, R2=%.4f",
            beta1, beta2, beta3, intercept, r_squared,
        )

    # ------------------------------------------------------------------
    # 訓練
    # ------------------------------------------------------------------

    @classmethod
    def train_from_history(
        cls,
        stock_id: str,
        dates: List[str],
        data_dir: str,
    ) -> VolumeProfilePredictor:
        """從歷史多天資料訓練迴歸模型。

        訓練資料：每天從 09:10 開始，每分鐘取一個樣本
        X = [V_ATO, V_current, elapsed_ratio]
        y = V_remain (= total_vol - V_current)

        Args:
            stock_id: 股票代碼。
            dates: 訓練日期列表 (YYYY-MM-DD)。
            data_dir: 特徵資料根目錄 (e.g. 'D:/feature_data/feature')。

        Returns:
            訓練完成的 VolumeProfilePredictor。
        """
        all_X: List[np.ndarray] = []
        all_y: List[float] = []

        for date in dates:
            samples = cls._extract_daily_samples(stock_id, date, data_dir)
            if samples is not None:
                X_day, y_day = samples
                all_X.append(X_day)
                all_y.extend(y_day)

        if not all_X:
            logger.warning(
                "無可用訓練資料 (%s)，回傳預設模型", stock_id
            )
            return cls._default_predictor()

        X = np.vstack(all_X)
        y = np.array(all_y)

        logger.info(
            "Volume profile 訓練: %d 天, %d 個樣本", len(dates), len(y)
        )

        # Ordinary Least Squares: [X, 1] @ [beta; intercept] = y
        ones = np.ones((X.shape[0], 1))
        X_aug = np.hstack([X, ones])

        # numpy least squares
        result, residuals, rank, sv = np.linalg.lstsq(X_aug, y, rcond=None)

        beta1, beta2, beta3, intercept = result

        # 計算 R-squared
        y_pred = X_aug @ result
        ss_res = np.sum((y - y_pred) ** 2)
        ss_tot = np.sum((y - np.mean(y)) ** 2)
        r_squared = 1.0 - ss_res / ss_tot if ss_tot > 0 else 0.0

        return cls(beta1, beta2, beta3, intercept, r_squared)

    @classmethod
    def _extract_daily_samples(
        cls,
        stock_id: str,
        date: str,
        data_dir: str,
    ) -> Optional[Tuple[np.ndarray, List[float]]]:
        """從單日資料中提取訓練樣本。"""
        filepath = os.path.join(data_dir, date, f"{stock_id}.parquet")
        if not os.path.exists(filepath):
            return None

        try:
            df = pd.read_parquet(filepath)
        except Exception as exc:
            logger.debug("無法讀取 %s: %s", filepath, exc)
            return None

        if 'time' not in df.columns or 'volume' not in df.columns:
            return None

        df['time'] = pd.to_datetime(df['time'])

        # 只取 Trade 記錄
        if 'type' in df.columns:
            df = df[df['type'] == 'Trade'].copy()

        if df.empty:
            return None

        # 篩選交易時段
        t_0900 = pd.Timestamp(f"{date} 09:00:00")
        t_0905 = pd.Timestamp(f"{date} 09:05:00")
        t_0910 = pd.Timestamp(f"{date} 09:10:00")
        t_1330 = pd.Timestamp(f"{date} 13:30:00")

        trading = df[(df['time'] >= t_0900) & (df['time'] <= t_1330)].copy()
        if trading.empty:
            return None

        # ATO 量 (09:00~09:05)
        ato_mask = trading['time'] < t_0905
        v_ato = float(trading.loc[ato_mask, 'volume'].sum())

        # 全日總量
        total_vol = float(trading['volume'].sum())
        if total_vol <= 0:
            return None

        # 逐分鐘累積量
        trading = trading.set_index('time')
        minute_cum = trading['volume'].resample('1min').sum().cumsum()

        # 從 09:10 開始取樣
        samples_after = minute_cum[minute_cum.index >= t_0910]
        if samples_after.empty:
            return None

        X_list: List[List[float]] = []
        y_list: List[float] = []

        for ts, v_current in samples_after.items():
            minutes_from_open = (ts - t_0900).total_seconds() / 60.0
            elapsed_ratio = minutes_from_open / _TOTAL_TRADING_MINUTES
            v_remain = total_vol - float(v_current)

            X_list.append([v_ato, float(v_current), elapsed_ratio])
            y_list.append(v_remain)

        if not X_list:
            return None

        return np.array(X_list), y_list

    @classmethod
    def _default_predictor(cls) -> VolumeProfilePredictor:
        """無訓練資料時的預設 TWAP-like 模型。"""
        return _DefaultPredictor()

    # ------------------------------------------------------------------
    # 預測
    # ------------------------------------------------------------------

    def predict_remaining(
        self,
        v_ato: float,
        v_current: float,
        elapsed_ratio: float,
    ) -> float:
        """預測從現在到收盤的剩餘量。

        Args:
            v_ato: ATO 量 (09:00~09:05 累積量)。
            v_current: 當前累積市場量。
            elapsed_ratio: 已過交易時間占比 (0~1)。

        Returns:
            預測剩餘量（保證非負）。
        """
        v_remain = (
            self.beta1 * v_ato
            + self.beta2 * v_current
            + self.beta3 * elapsed_ratio
            + self.intercept
        )
        return max(v_remain, 0.0)

    def predict_total(
        self,
        v_ato: float,
        v_current: float,
        elapsed_ratio: float,
    ) -> float:
        """預測全日總量。V_hat = V_current + V_remain_predicted"""
        return v_current + self.predict_remaining(v_ato, v_current, elapsed_ratio)

    # ------------------------------------------------------------------
    # 子委託量計算
    # ------------------------------------------------------------------

    def calculate_child_order_volume(
        self,
        delta_market: float,
        v_hat: float,
        v_prev: float,
        q_remaining: float,
    ) -> float:
        """論文公式 (3)：計算該分鐘的子委託量。

        vi = clip( (delta_market / (V_hat - V_prev)) * Q_remaining, 0, Q_remaining )

        Args:
            delta_market: 該分鐘市場成交量。
            v_hat: 預測全日總量。
            v_prev: 上一分鐘累積市場量。
            q_remaining: 剩餘目標量。

        Returns:
            子委託量（保證在 [0, q_remaining] 範圍內）。
        """
        if q_remaining <= 0 or delta_market <= 0:
            return 0.0

        denominator = v_hat - v_prev
        if denominator <= 0:
            return min(delta_market, q_remaining)

        ratio = delta_market / denominator
        child_vol = ratio * q_remaining

        return min(max(child_vol, 0.0), q_remaining)


# ======================================================================
# 預設 TWAP fallback 預測器
# ======================================================================


class _DefaultPredictor(VolumeProfilePredictor):
    """無歷史資料時的 TWAP fallback 預測器。

    不依賴迴歸係數，直接用 V_current / elapsed_ratio 估算全日量。
    """

    def __init__(self) -> None:
        super().__init__(
            beta1=0.0, beta2=0.0, beta3=0.0, intercept=0.0, r_squared=0.0
        )
        logger.info("使用預設 TWAP fallback 預測器（無歷史訓練資料）")

    def predict_remaining(
        self, v_ato: float, v_current: float, elapsed_ratio: float,
    ) -> float:
        if elapsed_ratio <= 0 or v_current <= 0:
            return 0.0
        estimated_total = v_current / elapsed_ratio
        return max(estimated_total - v_current, 0.0)

    def predict_total(
        self, v_ato: float, v_current: float, elapsed_ratio: float,
    ) -> float:
        if elapsed_ratio <= 0 or v_current <= 0:
            return max(v_current, 0.0)
        return v_current / elapsed_ratio
