"""VWAP 策略設定載入模組

從 YAML 檔案載入並解析 VWAP 執行策略的回測參數。
支援 Adaptive VWAP 區段設定。

YAML 結構範例::

    vwap:
      is_buy: true
      total_volume_quote: 10000000
      price_spread_levels: 3
      start_time: "09:00:00"
      end_time: "13:25:00"
      max_single_order_lots: 50
      min_order_lots: 1

    adaptive:
      enabled: true
      ato_end_time: "09:05:00"
      volume_lookback_days: 20
      mo_lo_lookback_ticks: 100
      mo_lo_threshold: 0.5
      order_start_time: "09:10:00"

    output:
      output_path: "D:/回測結果/VWAP"
      generate_html: true
      generate_png: true

    close_prices:
      file: "close.parquet"
"""

from __future__ import annotations

import logging
from datetime import time as dt_time
from typing import Any, Dict

import yaml

logger = logging.getLogger(__name__)


class VWAPConfig:
    """VWAP 策略設定容器。"""

    # ------------------------------------------------------------------
    # 預設值常數
    # ------------------------------------------------------------------
    _DEFAULTS_VWAP: Dict[str, Any] = {
        "is_buy": True,
        "total_volume_quote": 10_000_000,
        "price_spread_levels": 3,
        "start_time": "09:00:00",
        "end_time": "13:25:00",
        "max_single_order_lots": 50,
        "min_order_lots": 1,
    }

    _DEFAULTS_ADAPTIVE: Dict[str, Any] = {
        "enabled": True,
        "ato_end_time": "09:05:00",
        "volume_lookback_days": 20,
        "mo_lo_lookback_ticks": 100,
        "mo_lo_threshold": 0.5,
        "order_start_time": "09:10:00",
    }

    _DEFAULTS_OUTPUT: Dict[str, Any] = {
        "output_path": "D:/回測結果/VWAP",
        "generate_html": True,
        "generate_png": True,
    }

    _DEFAULTS_CLOSE: Dict[str, Any] = {
        "file": "close.parquet",
    }

    def __init__(self, config_path: str) -> None:
        logger.info("載入 VWAP 設定檔: %s", config_path)

        with open(config_path, "r", encoding="utf-8") as f:
            raw: Dict[str, Any] = yaml.safe_load(f) or {}

        self.raw_config: Dict[str, Any] = raw

        vwap = raw.get("vwap", {})
        adaptive = raw.get("adaptive", {})
        output = raw.get("output", {})
        close = raw.get("close_prices", {})

        # ---- VWAP 策略參數 ----
        self.is_buy: bool = vwap.get("is_buy", self._DEFAULTS_VWAP["is_buy"])
        self.total_volume_quote: float = float(
            vwap.get("total_volume_quote", self._DEFAULTS_VWAP["total_volume_quote"])
        )
        self.price_spread_levels: int = int(
            vwap.get("price_spread_levels", self._DEFAULTS_VWAP["price_spread_levels"])
        )
        self.start_time: dt_time = self._parse_time(
            vwap.get("start_time", self._DEFAULTS_VWAP["start_time"])
        )
        self.end_time: dt_time = self._parse_time(
            vwap.get("end_time", self._DEFAULTS_VWAP["end_time"])
        )
        self.max_single_order_lots: int = int(
            vwap.get("max_single_order_lots", self._DEFAULTS_VWAP["max_single_order_lots"])
        )
        self.min_order_lots: int = int(
            vwap.get("min_order_lots", self._DEFAULTS_VWAP["min_order_lots"])
        )

        # ---- Adaptive 參數 ----
        self.adaptive_enabled: bool = adaptive.get(
            "enabled", self._DEFAULTS_ADAPTIVE["enabled"]
        )
        self.ato_end_time: dt_time = self._parse_time(
            adaptive.get("ato_end_time", self._DEFAULTS_ADAPTIVE["ato_end_time"])
        )
        self.volume_lookback_days: int = int(
            adaptive.get("volume_lookback_days", self._DEFAULTS_ADAPTIVE["volume_lookback_days"])
        )
        self.mo_lo_lookback_ticks: int = int(
            adaptive.get("mo_lo_lookback_ticks", self._DEFAULTS_ADAPTIVE["mo_lo_lookback_ticks"])
        )
        self.mo_lo_threshold: float = float(
            adaptive.get("mo_lo_threshold", self._DEFAULTS_ADAPTIVE["mo_lo_threshold"])
        )
        self.order_start_time: dt_time = self._parse_time(
            adaptive.get("order_start_time", self._DEFAULTS_ADAPTIVE["order_start_time"])
        )

        # ---- 輸出設定 ----
        self.output_path: str = output.get(
            "output_path", self._DEFAULTS_OUTPUT["output_path"]
        )
        self.generate_html: bool = output.get(
            "generate_html", self._DEFAULTS_OUTPUT["generate_html"]
        )
        self.generate_png: bool = output.get(
            "generate_png", self._DEFAULTS_OUTPUT["generate_png"]
        )

        # ---- 收盤價檔案 ----
        self.close_prices_file: str = close.get(
            "file", self._DEFAULTS_CLOSE["file"]
        )

        logger.info(
            "VWAP 設定載入完成: is_buy=%s, total_volume_quote=%.0f, "
            "spread_levels=%d, window=%s~%s, adaptive=%s",
            self.is_buy,
            self.total_volume_quote,
            self.price_spread_levels,
            self.start_time,
            self.end_time,
            self.adaptive_enabled,
        )

    # ------------------------------------------------------------------
    # 時間解析
    # ------------------------------------------------------------------

    @staticmethod
    def _parse_time(time_str: str) -> dt_time:
        parts = time_str.split(":")
        if len(parts) != 3:
            raise ValueError(
                f"時間格式錯誤，預期 'HH:MM:SS'，收到 '{time_str}'"
            )
        return dt_time(int(parts[0]), int(parts[1]), int(parts[2]))

    # ------------------------------------------------------------------
    # 字典轉換
    # ------------------------------------------------------------------

    def get_vwap_config(self) -> Dict[str, Any]:
        """將 VWAP 策略參數轉為字典，供 VWAPLoop / VWAPOrderLogic 使用。"""
        return {
            "is_buy": self.is_buy,
            "total_volume_quote": self.total_volume_quote,
            "price_spread_levels": self.price_spread_levels,
            "start_time": self.start_time,
            "end_time": self.end_time,
            "max_single_order_lots": self.max_single_order_lots,
            "min_order_lots": self.min_order_lots,
            # Adaptive 參數
            "adaptive_enabled": self.adaptive_enabled,
            "ato_end_time": self.ato_end_time,
            "volume_lookback_days": self.volume_lookback_days,
            "mo_lo_lookback_ticks": self.mo_lo_lookback_ticks,
            "mo_lo_threshold": self.mo_lo_threshold,
            "order_start_time": self.order_start_time,
        }

    # ------------------------------------------------------------------
    # 命令列覆蓋
    # ------------------------------------------------------------------

    _CLI_ARG_MAPPING: Dict[str, str] = {
        "total_volume": "total_volume_quote",
    }

    def override(self, **kwargs: Any) -> None:
        """以命令列引數覆蓋設定值。"""
        for arg_name, attr_name in self._CLI_ARG_MAPPING.items():
            value = kwargs.get(arg_name)
            if value is not None:
                target_type = type(getattr(self, attr_name))
                setattr(self, attr_name, target_type(value))
                logger.info("覆蓋設定 %s = %s", attr_name, value)

    def __repr__(self) -> str:
        return (
            f"VWAPConfig(is_buy={self.is_buy}, "
            f"total_volume_quote={self.total_volume_quote}, "
            f"spread_levels={self.price_spread_levels}, "
            f"window={self.start_time}~{self.end_time}, "
            f"adaptive={self.adaptive_enabled})"
        )
