"""
早盤策略專用圖表產生器
只顯示關鍵進場條件：大量搓合金額 + 五檔掛單比率
"""

import logging
import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots
from typing import Dict, List, Optional

logger = logging.getLogger(__name__)


class EarlyChartCreator:
    """早盤策略圖表建立器（簡化版）"""

    def __init__(self, config: Optional[Dict] = None):
        """
        初始化圖表建立器

        Args:
            config: 設定字典
        """
        self.config = config or {}

    def create_chart(
        self,
        df: pd.DataFrame,
        trade_record: Dict,
        output_path: str,
        stock_id: str,
        date: str,
        massive_threshold: float
    ) -> str:
        """
        建立早盤策略視覺化圖表（簡化版）

        Args:
            df: 資料 DataFrame
            trade_record: 交易記錄
            output_path: HTML 輸出路徑
            stock_id: 股票代碼
            date: 日期
            massive_threshold: 大量搓合門檻（元）

        Returns:
            輸出路徑
        """
        # 過濾有效價格資料
        df_valid = df[df['price'] > 0].copy() if 'price' in df.columns else df.copy()

        if len(df_valid) < 10:
            logger.warning(f"{stock_id} 有效資料過少，跳過圖表產生")
            return None

        # 建立子圖（3行1列）
        fig = make_subplots(
            rows=3, cols=1,
            shared_xaxes=True,
            vertical_spacing=0.05,
            row_heights=[0.50, 0.25, 0.25],
            subplot_titles=(
                f'{stock_id} 早盤進場策略 (09:01-09:05) - {date}',
                '大量搓合金額 (萬元)',
                '五檔掛單比率 (委賣/委買)'
            )
        )

        # 子圖1：價格 + Day High + 進出場點 + 移動停利低點
        self._add_price_chart(fig, df_valid, trade_record, row=1, col=1)

        # 子圖2：大量搓合金額
        self._add_massive_matching_chart(fig, df, massive_threshold, row=2, col=1)

        # 子圖3：五檔掛單比率
        self._add_orderbook_ratio_chart(fig, df, row=3, col=1)

        # 更新佈局
        self._update_layout(fig, stock_id, date)

        # 儲存圖表
        fig.write_html(
            output_path,
            config={'displayModeBar': True, 'scrollZoom': True}
        )
        logger.info(f"圖表已儲存: {output_path}")

        return output_path

    def _add_price_chart(self, fig, df, trade_record, row, col):
        """添加價格圖表"""
        # 計算 Y 軸範圍
        price_min = df['price'].min()
        price_max = df['price'].max()

        if 'day_high' in df.columns:
            price_max = max(price_max, df['day_high'].max())

        price_range = price_max - price_min
        y_min = max(0, price_min - price_range * 0.05)
        y_max = price_max + price_range * 0.05

        # 價格線
        fig.add_trace(
            go.Scatter(
                x=df['time'], y=df['price'],
                mode='lines',
                name='價格',
                line=dict(color='black', width=1.5),
                hovertemplate='價格: %{y:.2f}<br>時間: %{x}'
            ),
            row=row, col=col
        )

        # Day High 線
        if 'day_high' in df.columns:
            fig.add_trace(
                go.Scatter(
                    x=df['time'], y=df['day_high'],
                    mode='lines',
                    name='Day High',
                    line=dict(color='red', width=2, dash='solid'),
                    opacity=0.7,
                    hovertemplate='Day High: %{y:.2f}<br>時間: %{x}'
                ),
                row=row, col=col
            )

        # 移動停利低點線
        colors = {'low_1m': 'green', 'low_3m': 'orange', 'low_5m': 'purple'}
        names = {'low_1m': '1分鐘低點', 'low_3m': '3分鐘低點', 'low_5m': '5分鐘低點'}

        for col_name, color in colors.items():
            if col_name in df.columns:
                fig.add_trace(
                    go.Scatter(
                        x=df['time'], y=df[col_name],
                        mode='lines',
                        name=names[col_name],
                        line=dict(color=color, width=1, dash='dash'),
                        opacity=0.6,
                        hovertemplate=f'{names[col_name]}: %{{y:.2f}}<br>時間: %{{x}}'
                    ),
                    row=row, col=col
                )

        # 設定 Y 軸範圍
        fig.update_yaxes(range=[y_min, y_max], row=row, col=col)

        # 標記進場點
        entry_price = trade_record.get('entry_price', 0)
        entry_time = trade_record.get('entry_time', '')
        entry_ratio = trade_record.get('entry_ratio', 0)

        if entry_price > 0:
            fig.add_trace(
                go.Scatter(
                    x=[entry_time], y=[entry_price],
                    mode='markers+text',
                    name='進場',
                    marker=dict(color='red', size=20, symbol='circle'),
                    text=['進場'],
                    textposition='top center',
                    hovertemplate=f"進場<br>價格: {entry_price:.2f}<br>Ratio: {entry_ratio:.1f}<br>時間: %{{x}}"
                ),
                row=row, col=col
            )

        # 標記出場點（移動停利三批出場）
        exit_colors = {'1min': 'green', '3min': 'orange', '5min': 'purple'}

        if 'exits' in trade_record:
            for i, exit_info in enumerate(trade_record['exits']):
                exit_time = exit_info.get('time', '')
                exit_price = exit_info.get('price', 0)
                exit_reason = exit_info.get('reason', '')
                exit_ratio = exit_info.get('ratio', 0)

                # 判斷出場類型
                level_name = None
                for key in ['1min', '3min', '5min']:
                    if key in exit_reason:
                        level_name = key
                        break

                exit_color = exit_colors.get(level_name, 'gray')
                exit_label = f'{level_name}停利' if level_name else '出場'

                fig.add_trace(
                    go.Scatter(
                        x=[exit_time], y=[exit_price],
                        mode='markers',
                        name=exit_label if i == 0 else None,
                        showlegend=(i == 0),
                        marker=dict(color=exit_color, size=18, symbol='triangle-down'),
                        hovertemplate=f"{exit_label}<br>價格: {exit_price:.2f}<br>比例: {exit_ratio:.1%}<br>時間: %{{x}}"
                    ),
                    row=row, col=col
                )

    def _add_massive_matching_chart(self, fig, df, threshold, row, col):
        """添加大量搓合金額圖表"""
        # 檢查欄位
        amount_col = None
        for col_name in ['massive_matching_amount', 'outside_amount_1s', 'outside_1s']:
            if col_name in df.columns:
                amount_col = col_name
                break

        if amount_col is None:
            logger.warning("找不到大量搓合金額欄位")
            return

        # 轉換為萬元
        amount_data = df[amount_col] / 10000
        threshold_wan = threshold / 10000

        # 大量搓合金額線
        fig.add_trace(
            go.Scatter(
                x=df['time'], y=amount_data,
                mode='lines',
                name='外盤金額',
                line=dict(color='blue', width=2),
                hovertemplate='外盤金額: %{y:.1f}萬<br>時間: %{x}'
            ),
            row=row, col=col
        )

        # 門檻線
        fig.add_hline(
            y=threshold_wan,
            line_dash="dash",
            line_color="red",
            line_width=2,
            annotation_text=f"門檻: {threshold_wan:.1f}萬",
            annotation_position="right",
            row=row, col=col
        )

        # 可進場區域（金額 >= 門檻）
        amount_high = amount_data.copy()
        amount_high[amount_high < threshold_wan] = None

        fig.add_trace(
            go.Scatter(
                x=df['time'], y=amount_high,
                fill='tozeroy',
                mode='none',
                name='符合門檻',
                fillcolor='rgba(0, 255, 0, 0.2)',
                showlegend=True
            ),
            row=row, col=col
        )

    def _add_orderbook_ratio_chart(self, fig, df, row, col):
        """添加五檔掛單比率圖表"""
        # 檢查欄位
        ratio_col = None
        for col_name in ['bid_ask_ratio', 'orderbook_ratio', 'ask_bid_ratio']:
            if col_name in df.columns:
                ratio_col = col_name
                break

        if ratio_col is None:
            logger.warning("找不到五檔掛單比率欄位")
            return

        # 掛單比率線
        fig.add_trace(
            go.Scatter(
                x=df['time'], y=df[ratio_col],
                mode='lines',
                name='委賣/委買',
                line=dict(color='purple', width=2),
                hovertemplate='委賣/委買: %{y:.2f}<br>時間: %{x}'
            ),
            row=row, col=col
        )

        # 門檻線（1.0）
        threshold = self.config.get('orderbook_bid_ask_ratio_min', 1.0)
        fig.add_hline(
            y=threshold,
            line_dash="dash",
            line_color="red",
            line_width=2,
            annotation_text=f"門檻: {threshold:.1f}",
            annotation_position="right",
            row=row, col=col
        )

        # 可進場區域（比率 >= 門檻）
        ratio_high = df[ratio_col].copy()
        ratio_high[ratio_high < threshold] = None

        fig.add_trace(
            go.Scatter(
                x=df['time'], y=ratio_high,
                fill='tozeroy',
                mode='none',
                name='符合門檻',
                fillcolor='rgba(0, 255, 0, 0.2)',
                showlegend=True
            ),
            row=row, col=col
        )

    def _update_layout(self, fig, stock_id, date):
        """更新圖表佈局"""
        fig.update_layout(
            title_text=f"{stock_id} 早盤進場策略 (09:01-09:05) - {date}",
            title_font_size=18,
            showlegend=True,
            height=900,
            hovermode='x unified',
            plot_bgcolor='white',
            paper_bgcolor='white'
        )

        # X 軸設定
        fig.update_xaxes(title_text="時間", row=3, col=1, showticklabels=True)
        fig.update_xaxes(showticklabels=False, row=1, col=1)
        fig.update_xaxes(showticklabels=False, row=2, col=1)
        fig.update_xaxes(showgrid=True, gridwidth=1, gridcolor='lightgray')

        # Y 軸設定
        fig.update_yaxes(title_text="價格", row=1, col=1)
        fig.update_yaxes(title_text="金額(萬)", row=2, col=1)
        fig.update_yaxes(title_text="比率", row=3, col=1)
        fig.update_yaxes(showgrid=True, gridwidth=1, gridcolor='lightgray')
