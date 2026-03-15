"""Dataclass models for the VWAP execution strategy backtest module.

This module defines all data structures used by the Taiwan stock market
VWAP (Volume Weighted Average Price) execution algorithm simulator.

Key market conventions:
    - 1 lot = 1000 shares in Taiwan stock market
    - All monetary amounts are denominated in TWD
    - VWAP = Volume Weighted Average Price
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime
from typing import List, Tuple


@dataclass
class VWAPOrder:
    """Single fill record for a VWAP execution order.

    Attributes:
        order_id: Unique identifier for this fill.
        time: Timestamp when the fill occurred.
        fill_price: Execution price per share.
        fill_lots: Number of lots filled (1 lot = 1000 shares).
        fill_amount_twd: Total fill amount in TWD (fill_price * fill_lots * 1000).
        mid_price: Market mid price at the time of the fill.
        ask_depth_available: Ask-side depth available within the spread (lots).
        cumulative_lots: Cumulative filled lots up to and including this fill.
        cumulative_amount: Cumulative filled amount (TWD) up to and including this fill.
        delta: Completion ratio at this point (0.0 ~ 1.0).
        running_vwap: Running VWAP price at this point in the execution.
    """

    order_id: int
    time: datetime
    fill_price: float
    fill_lots: float
    fill_amount_twd: float
    mid_price: float
    ask_depth_available: float
    cumulative_lots: float
    cumulative_amount: float
    delta: float
    running_vwap: float
    # Adaptive VWAP 擴充欄位
    order_type: str = "taker"
    mo_ratio_at_fill: float = 0.0
    minute_market_vol: float = 0.0
    predicted_total_vol: float = 0.0


@dataclass
class VWAPExecutionState:
    """Mutable state tracker for an ongoing VWAP execution session.

    Tracks cumulative fills, market snapshots, and completion progress
    throughout the execution window.

    Attributes:
        target_lots: Target total lots to purchase.
        volume_remaining: Lots still remaining to be filled.
        total_filled_lots: Cumulative lots filled so far.
        total_spent_amount: Cumulative amount spent in TWD.
        start_price: First trade price observed when execution started.
        current_vwap: Current execution VWAP price.
        delta: Completion ratio (0.0 ~ 1.0).
        orders: Chronological list of all fill records.
        market_total_volume: Market-wide total trade volume in lots.
        market_total_amount: Market-wide total trade amount in TWD.
        is_complete: Whether the execution has been finalized.
        market_prices: Time series of market prices (timestamp, price).
        market_bid_depth: Time series of bid 5-level depth (timestamp, depth).
        market_ask_depth: Time series of ask 5-level depth (timestamp, depth).
    """

    target_lots: float = 0.0
    volume_remaining: float = 0.0
    total_filled_lots: float = 0.0
    total_spent_amount: float = 0.0
    start_price: float = 0.0
    current_vwap: float = 0.0
    delta: float = 0.0
    orders: List[VWAPOrder] = field(default_factory=list)
    market_total_volume: float = 0.0
    market_total_amount: float = 0.0
    is_complete: bool = False
    market_prices: List[Tuple[datetime, float]] = field(default_factory=list)
    market_bid_depth: List[Tuple[datetime, float]] = field(default_factory=list)
    market_ask_depth: List[Tuple[datetime, float]] = field(default_factory=list)

    def add_fill(self, order: VWAPOrder) -> None:
        """Update execution state after a new fill.

        Recalculates cumulative totals, VWAP, and completion ratio,
        then appends the order to the fill history.

        Args:
            order: The fill record to incorporate into the state.
        """
        self.total_filled_lots += order.fill_lots
        self.total_spent_amount += order.fill_amount_twd
        self.volume_remaining = max(self.volume_remaining - order.fill_lots, 0.0)

        if self.total_filled_lots > 0:
            self.current_vwap = self.total_spent_amount / (self.total_filled_lots * 1000)

        if self.target_lots > 0:
            self.delta = self.total_filled_lots / self.target_lots

        self.orders.append(order)

    def finalize(self) -> None:
        """Mark the execution as complete.

        Sets ``is_complete`` to ``True`` when the completion ratio
        indicates the target has been fully filled (delta >= 1.0).
        """
        self.is_complete = self.delta >= 1.0


@dataclass
class VWAPMetrics:
    """Final summary statistics for a completed VWAP execution.

    Provides a comprehensive snapshot of execution quality metrics
    including slippage analysis, participation rate, and timing.

    Attributes:
        stock_id: Taiwan stock ticker symbol (e.g. "2330").
        date: Execution date string (e.g. "2025-01-15").
        vwap_price: Our execution VWAP price.
        market_vwap: Market-wide VWAP price for comparison.
        start_price: First trade price when execution started.
        slippage_bps_vs_start: Slippage versus start price in basis points.
        slippage_bps_vs_market: Slippage versus market VWAP in basis points.
        target_lots: Original target lots to purchase.
        total_filled_lots: Actual total lots filled.
        completion_ratio: Fill completion ratio (0.0 ~ 1.0).
        total_spent_amount: Total amount spent in TWD.
        participation_rate: Our volume as a fraction of market volume.
        total_orders: Total number of individual fills.
        execution_duration_minutes: Wall-clock execution duration in minutes.
    """

    stock_id: str
    date: str
    vwap_price: float
    market_vwap: float
    start_price: float
    slippage_bps_vs_start: float
    slippage_bps_vs_market: float
    target_lots: float
    total_filled_lots: float
    completion_ratio: float
    total_spent_amount: float
    participation_rate: float
    total_orders: int
    execution_duration_minutes: float
    # Adaptive VWAP 擴充指標
    avg_mo_ratio: float = 0.0
    taker_order_count: int = 0
    maker_order_count: int = 0
    volume_prediction_r2: float = 0.0
