"""VWAP 執行策略視覺化模組 (Adaptive 版)

六個子圖：
    1. 價格 + VWAP + 下單點 (taker=紅, maker=綠)
    2. 完成進度 Delta (%)
    3. 掛單簿深度（張）
    4. 下單量（張）+ 累計量
    5. MO ratio 時序圖
    6. 每分鐘市場量 vs 下單量

輸出格式：HTML / PNG
"""

from __future__ import annotations

import logging
import os
from typing import Optional

import plotly.graph_objects as go
from plotly.subplots import make_subplots

from strategy_modules.vwap_models import VWAPExecutionState, VWAPMetrics

logger = logging.getLogger(__name__)

_CHART_WIDTH = 1400
_CHART_HEIGHT = 1400
_MAX_MARKET_PRICE_POINTS = 5000
_MARKER_SIZE_MIN = 5
_MARKER_SIZE_MAX = 20


class VWAPChartCreator:
    """VWAP Adaptive 執行策略視覺化"""

    @staticmethod
    def create_chart(
        state: VWAPExecutionState,
        metrics: VWAPMetrics,
        stock_id: str,
        date: str,
        company_name: str,
        output_dir: str,
        generate_html: bool = True,
        generate_png: bool = True,
    ) -> Optional[str]:
        if not state.orders:
            logger.warning("無委託資料，跳過圖表產出。")
            return None

        os.makedirs(output_dir, exist_ok=True)

        fig = VWAPChartCreator._build_figure(state, metrics, stock_id, date, company_name)

        html_path: Optional[str] = None

        if generate_html:
            html_path = os.path.join(output_dir, f"{stock_id}_vwap_{date}.html")
            fig.write_html(html_path)
            logger.info("HTML 圖表已儲存: %s", html_path)

        if generate_png:
            png_path = os.path.join(output_dir, f"{stock_id}_vwap_{date}.png")
            try:
                fig.write_image(png_path)
                logger.info("PNG 圖表已儲存: %s", png_path)
            except (ImportError, ValueError) as exc:
                logger.warning("PNG 產出失敗（需安裝 kaleido）: %s", exc)

        return html_path

    # ------------------------------------------------------------------
    # 內部建構方法
    # ------------------------------------------------------------------

    @staticmethod
    def _build_figure(
        state: VWAPExecutionState,
        metrics: VWAPMetrics,
        stock_id: str,
        date: str,
        company_name: str,
    ) -> go.Figure:
        """組裝六子圖。"""

        fig = make_subplots(
            rows=6,
            cols=1,
            shared_xaxes=True,
            vertical_spacing=0.03,
            row_heights=[0.30, 0.14, 0.14, 0.14, 0.14, 0.14],
            subplot_titles=[
                "價格與VWAP",
                "完成進度(%)",
                "掛單簿深度(張)",
                "下單量(張)",
                "MO Ratio",
                "分鐘市場量 vs 下單量",
            ],
            specs=[
                [{"secondary_y": False}],
                [{"secondary_y": False}],
                [{"secondary_y": False}],
                [{"secondary_y": True}],
                [{"secondary_y": False}],
                [{"secondary_y": False}],
            ],
        )

        VWAPChartCreator._add_price_subplot(fig, state, metrics)
        VWAPChartCreator._add_delta_subplot(fig, state)
        VWAPChartCreator._add_depth_subplot(fig, state)
        VWAPChartCreator._add_volume_subplot(fig, state)
        VWAPChartCreator._add_mo_ratio_subplot(fig, state)
        VWAPChartCreator._add_minute_volume_subplot(fig, state)
        VWAPChartCreator._add_metrics_annotation(fig, metrics)

        fig.update_layout(
            title=dict(
                text=f"Adaptive VWAP 執行分析 — {stock_id} {company_name} ({date})",
                x=0.5,
                xanchor="center",
            ),
            height=_CHART_HEIGHT,
            width=_CHART_WIDTH,
            plot_bgcolor="white",
            paper_bgcolor="white",
            showlegend=True,
            legend=dict(
                orientation="h",
                yanchor="bottom",
                y=1.02,
                xanchor="right",
                x=1,
            ),
        )

        for row_idx in range(1, 7):
            y_key = f"yaxis{'' if row_idx == 1 else row_idx}"
            fig.update_layout(
                **{y_key: dict(
                    gridcolor="rgba(200,200,200,0.4)",
                    zerolinecolor="rgba(200,200,200,0.6)",
                )}
            )
            x_key = f"xaxis{'' if row_idx == 1 else row_idx}"
            fig.update_layout(
                **{x_key: dict(gridcolor="rgba(200,200,200,0.4)")}
            )

        return fig

    # ------------------------------------------------------------------
    # Subplot 1: 價格 + VWAP + 下單點 (taker/maker 顏色區分)
    # ------------------------------------------------------------------

    @staticmethod
    def _add_price_subplot(
        fig: go.Figure,
        state: VWAPExecutionState,
        metrics: VWAPMetrics,
    ) -> None:
        # 市場成交價（降采樣）
        market_times, market_prices = VWAPChartCreator._downsample_series(
            state.market_prices
        )

        if market_times:
            fig.add_trace(
                go.Scatter(
                    x=market_times, y=market_prices,
                    mode="lines", name="市場成交價",
                    line=dict(color="black", width=1),
                ),
                row=1, col=1,
            )

        # 策略 Running VWAP
        order_times = [o.time for o in state.orders]
        running_vwaps = [o.running_vwap for o in state.orders]

        fig.add_trace(
            go.Scatter(
                x=order_times, y=running_vwaps,
                mode="lines", name="策略 VWAP",
                line=dict(color="blue", width=2, dash="dash"),
            ),
            row=1, col=1,
        )

        # 起始價水平線
        if market_times and metrics.start_price > 0:
            fig.add_trace(
                go.Scatter(
                    x=[market_times[0], market_times[-1]],
                    y=[metrics.start_price, metrics.start_price],
                    mode="lines",
                    name=f"起始價 ({metrics.start_price:.2f})",
                    line=dict(color="gray", width=1.5, dash="dot"),
                ),
                row=1, col=1,
            )

        # 下單標記 — Taker (紅) vs Maker (綠)
        taker_orders = [o for o in state.orders if o.order_type == "taker"]
        maker_orders = [o for o in state.orders if o.order_type == "maker"]

        for orders, color, name in [
            (taker_orders, "rgba(255, 50, 50, 0.7)", "Taker 下單"),
            (maker_orders, "rgba(50, 180, 50, 0.7)", "Maker 下單"),
        ]:
            if not orders:
                continue

            times = [o.time for o in orders]
            prices = [o.fill_price for o in orders]
            lots = [o.fill_lots for o in orders]
            sizes = VWAPChartCreator._compute_marker_sizes(lots)

            hover_texts = [
                (
                    f"時間: {o.time}<br>"
                    f"成交價: {o.fill_price:.2f}<br>"
                    f"成交張數: {o.fill_lots:.1f}<br>"
                    f"類型: {o.order_type}<br>"
                    f"MO ratio: {o.mo_ratio_at_fill:.3f}<br>"
                    f"Running VWAP: {o.running_vwap:.2f}"
                )
                for o in orders
            ]

            fig.add_trace(
                go.Scatter(
                    x=times, y=prices,
                    mode="markers", name=name,
                    marker=dict(
                        size=sizes, color=color,
                        line=dict(color=color.replace("0.7", "1.0"), width=1),
                        symbol="circle",
                    ),
                    text=hover_texts, hoverinfo="text",
                ),
                row=1, col=1,
            )

    # ------------------------------------------------------------------
    # Subplot 2: 完成進度
    # ------------------------------------------------------------------

    @staticmethod
    def _add_delta_subplot(fig: go.Figure, state: VWAPExecutionState) -> None:
        order_times = [o.time for o in state.orders]
        delta_pcts = [o.delta * 100 for o in state.orders]

        fig.add_trace(
            go.Scatter(
                x=order_times, y=delta_pcts,
                mode="lines+markers", name="完成進度",
                line=dict(color="purple", width=1.5),
                marker=dict(size=3),
            ),
            row=2, col=1,
        )

        if order_times:
            for ref_val in (50, 100):
                fig.add_trace(
                    go.Scatter(
                        x=[order_times[0], order_times[-1]],
                        y=[ref_val, ref_val],
                        mode="lines",
                        line=dict(color="gray", width=1, dash="dash"),
                        showlegend=False, hoverinfo="skip",
                    ),
                    row=2, col=1,
                )

    # ------------------------------------------------------------------
    # Subplot 3: 掛單簿深度
    # ------------------------------------------------------------------

    @staticmethod
    def _add_depth_subplot(fig: go.Figure, state: VWAPExecutionState) -> None:
        ask_times, ask_depths = VWAPChartCreator._downsample_series(state.market_ask_depth)
        if ask_times:
            fig.add_trace(
                go.Scatter(
                    x=ask_times, y=ask_depths,
                    mode="lines", name="賣方深度",
                    line=dict(color="red", width=1),
                ),
                row=3, col=1,
            )

        bid_times, bid_depths = VWAPChartCreator._downsample_series(state.market_bid_depth)
        if bid_times:
            fig.add_trace(
                go.Scatter(
                    x=bid_times, y=bid_depths,
                    mode="lines", name="買方深度",
                    line=dict(color="green", width=1),
                ),
                row=3, col=1,
            )

    # ------------------------------------------------------------------
    # Subplot 4: 下單量 + 累計量
    # ------------------------------------------------------------------

    @staticmethod
    def _add_volume_subplot(fig: go.Figure, state: VWAPExecutionState) -> None:
        order_times = [o.time for o in state.orders]
        fill_lots = [o.fill_lots for o in state.orders]
        cumulative_lots = [o.cumulative_lots for o in state.orders]

        # 長條圖顏色按 taker/maker
        colors = [
            "rgba(255, 50, 50, 0.7)" if o.order_type == "taker"
            else "rgba(50, 180, 50, 0.7)"
            for o in state.orders
        ]

        fig.add_trace(
            go.Bar(
                x=order_times, y=fill_lots,
                name="單筆成交(張)",
                marker=dict(color=colors),
            ),
            row=4, col=1, secondary_y=False,
        )

        fig.add_trace(
            go.Scatter(
                x=order_times, y=cumulative_lots,
                mode="lines", name="累計成交(張)",
                line=dict(color="orange", width=2),
            ),
            row=4, col=1, secondary_y=True,
        )

        fig.update_yaxes(title_text="單筆(張)", row=4, col=1, secondary_y=False)
        fig.update_yaxes(title_text="累計(張)", row=4, col=1, secondary_y=True)

    # ------------------------------------------------------------------
    # Subplot 5: MO ratio 時序圖
    # ------------------------------------------------------------------

    @staticmethod
    def _add_mo_ratio_subplot(fig: go.Figure, state: VWAPExecutionState) -> None:
        order_times = [o.time for o in state.orders]
        mo_ratios = [o.mo_ratio_at_fill for o in state.orders]

        fig.add_trace(
            go.Scatter(
                x=order_times, y=mo_ratios,
                mode="lines+markers", name="MO Ratio",
                line=dict(color="teal", width=1.5),
                marker=dict(size=3),
            ),
            row=5, col=1,
        )

        # 0.5 門檻線
        if order_times:
            fig.add_trace(
                go.Scatter(
                    x=[order_times[0], order_times[-1]],
                    y=[0.5, 0.5],
                    mode="lines",
                    name="MO 門檻 (0.5)",
                    line=dict(color="red", width=1, dash="dash"),
                    showlegend=True,
                ),
                row=5, col=1,
            )

    # ------------------------------------------------------------------
    # Subplot 6: 每分鐘市場量 vs 我們下單量
    # ------------------------------------------------------------------

    @staticmethod
    def _add_minute_volume_subplot(
        fig: go.Figure,
        state: VWAPExecutionState,
    ) -> None:
        order_times = [o.time for o in state.orders]
        minute_market_vols = [o.minute_market_vol for o in state.orders]
        fill_lots = [o.fill_lots for o in state.orders]

        fig.add_trace(
            go.Bar(
                x=order_times, y=minute_market_vols,
                name="分鐘市場量(張)",
                marker=dict(color="rgba(100, 149, 237, 0.5)"),
            ),
            row=6, col=1,
        )

        fig.add_trace(
            go.Bar(
                x=order_times, y=fill_lots,
                name="我方下單量(張)",
                marker=dict(color="rgba(255, 165, 0, 0.7)"),
            ),
            row=6, col=1,
        )

        fig.update_layout(barmode="group")

    # ------------------------------------------------------------------
    # 指標標註
    # ------------------------------------------------------------------

    @staticmethod
    def _add_metrics_annotation(fig: go.Figure, metrics: VWAPMetrics) -> None:
        taker_pct = (
            metrics.taker_order_count / metrics.total_orders * 100
            if metrics.total_orders > 0 else 0
        )
        annotation_text = (
            f"VWAP: {metrics.vwap_price:.2f}  |  "
            f"滑價: {metrics.slippage_bps_vs_market:+.1f}bps  |  "
            f"完成: {metrics.completion_ratio:.1%}  |  "
            f"Taker: {taker_pct:.0f}%  |  "
            f"R²: {metrics.volume_prediction_r2:.3f}"
        )

        fig.add_annotation(
            text=annotation_text,
            xref="paper", yref="paper",
            x=0.99, y=0.99,
            xanchor="right", yanchor="top",
            showarrow=False,
            font=dict(size=11, color="black"),
            bgcolor="rgba(255, 255, 255, 0.85)",
            bordercolor="gray", borderwidth=1, borderpad=6,
        )

    # ------------------------------------------------------------------
    # 工具方法
    # ------------------------------------------------------------------

    @staticmethod
    def _downsample_series(series: list[tuple]) -> tuple[list, list]:
        if not series:
            return [], []

        n = len(series)
        if n <= _MAX_MARKET_PRICE_POINTS:
            return [pt[0] for pt in series], [pt[1] for pt in series]

        step = n / _MAX_MARKET_PRICE_POINTS
        indices: list[int] = []
        current = 0.0
        while current < n:
            indices.append(int(current))
            current += step

        if indices[-1] != n - 1:
            indices.append(n - 1)

        seen: set[int] = set()
        unique_indices: list[int] = []
        for idx in indices:
            if idx not in seen:
                seen.add(idx)
                unique_indices.append(idx)

        return (
            [series[i][0] for i in unique_indices],
            [series[i][1] for i in unique_indices],
        )

    @staticmethod
    def _compute_marker_sizes(fill_lots: list[float]) -> list[float]:
        if not fill_lots:
            return []

        min_lots = min(fill_lots)
        max_lots = max(fill_lots)
        lot_range = max_lots - min_lots

        if lot_range == 0:
            mid_size = (_MARKER_SIZE_MIN + _MARKER_SIZE_MAX) / 2
            return [mid_size] * len(fill_lots)

        return [
            _MARKER_SIZE_MIN + (lots - min_lots) / lot_range * (_MARKER_SIZE_MAX - _MARKER_SIZE_MIN)
            for lots in fill_lots
        ]
