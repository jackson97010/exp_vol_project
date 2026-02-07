"""
策略視覺化模組

使用 Plotly 產生互動式 HTML 圖表，顯示價格走勢和進出場點
參考 run_backtest_single.py 的 subplot 版面配置
"""

import math
import logging
from pathlib import Path
from typing import List, Dict, Optional
import pandas as pd

logger = logging.getLogger(__name__)


def get_tick(price: float) -> float:
    """根據價格取得 tick 大小"""
    if price < 10:
        return 0.01
    elif price < 50:
        return 0.05
    elif price < 100:
        return 0.1
    elif price < 500:
        return 0.5
    elif price < 1000:
        return 1.0
    else:
        return 5.0


def calc_limit_up_price(ref_price: float, factor: float = 1.1) -> float:
    """計算漲停價（向下取整至 tick 倍數）"""
    if ref_price > 0:
        limit_up = ref_price * factor
        tick = get_tick(limit_up)
        adjusted = math.floor(limit_up / tick) * tick
        return round(adjusted, 2)
    return ref_price


def create_strategy_figure(
    stock_id: str,
    sector_name: str,
    price_data: pd.DataFrame,
    events: List[Dict],
    ref_price: float,
    ratio_data: pd.DataFrame = None,
    day_high_data: pd.DataFrame = None,
    exhaustion_info: List[Dict] = None
):
    """
    建立策略視覺化圖表物件（可用於 HTML 或 PNG 輸出）

    Args:
        stock_id: 股票代碼
        sector_name: 族群名稱
        price_data: 價格資料 DataFrame (columns: datetime, price)
        events: 事件列表 (進場/出場)
        ref_price: 昨收價
        ratio_data: Ratio 資料 DataFrame (columns: datetime, ratio)
        day_high_data: Day High 資料 DataFrame (columns: datetime, day_high)
        exhaustion_info: 力竭出場資訊列表 (peak_ratio, drawdown, reset_count)

    Returns:
        plotly.graph_objects.Figure: 圖表物件
    """
    try:
        import plotly.graph_objects as go
        from plotly.subplots import make_subplots
    except ImportError:
        logger.error("需要安裝 plotly: pip install plotly")
        return None

    # 確保 datetime 欄位是正確的類型
    if 'datetime' in price_data.columns:
        price_data = price_data.copy()
        price_data['datetime'] = pd.to_datetime(price_data['datetime'])

    # 計算漲停價
    limit_up_price = calc_limit_up_price(ref_price)

    # 檢查是否有 ratio 資料
    has_ratio = ratio_data is not None and len(ratio_data) > 0

    # 建立圖表（3 個 subplots）
    fig = make_subplots(
        rows=3, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.03,
        row_heights=[0.5, 0.25, 0.25],
        subplot_titles=(
            f"{stock_id} [{sector_name}] (昨收: {ref_price:.2f}, 漲停: {limit_up_price:.2f})",
            "Ratio (藍線: 15s_300s 無加權 / 紫虛線: 15s_180s_w321 加權)",
            "漲跌幅 (%)"
        )
    )

    # ========================================
    # Subplot 1: 價格走勢
    # ========================================
    # 價格走勢線（黑線）
    fig.add_trace(
        go.Scattergl(
            x=price_data['datetime'],
            y=price_data['price'],
            mode='lines',
            name='價格',
            line=dict(color='black', width=1),
            hovertemplate='%{x|%H:%M:%S}<br>價格: %{y:.2f}<extra></extra>'
        ),
        row=1, col=1
    )

    # Day High 線
    if day_high_data is not None and len(day_high_data) > 0:
        fig.add_trace(
            go.Scatter(
                x=day_high_data['datetime'],
                y=day_high_data['day_high'],
                mode='lines',
                name='Day High',
                line=dict(color='#FF9800', width=2, dash='dot'),
                hovertemplate='%{x}<br>Day High: %{y:.2f}<extra></extra>'
            ),
            row=1, col=1
        )

    # 昨收價水平線
    if ref_price > 0:
        fig.add_hline(
            y=ref_price,
            line_dash="dash",
            line_color="gray",
            annotation_text=f"昨收: {ref_price:.2f}",
            annotation_position="right",
            row=1, col=1
        )

    # 漲停價水平線
    fig.add_hline(
        y=limit_up_price,
        line_dash="dash",
        line_color="red",
        annotation_text=f"漲停: {limit_up_price:.2f}",
        annotation_position="right",
        row=1, col=1
    )

    # 進場點標記（紅色上箭頭）
    entry_events = [e for e in events if e['type'] == 'entry']
    if entry_events:
        entry_times = [e['time'] for e in entry_events]
        entry_prices = [e['price'] for e in entry_events]
        entry_texts = [f"進場 ({e.get('entry_type', '')})" for e in entry_events]

        fig.add_trace(
            go.Scatter(
                x=entry_times,
                y=entry_prices,
                mode='markers',
                name='進場',
                marker=dict(
                    symbol='circle',
                    size=18,
                    color='red',
                    line=dict(color='white', width=2)
                ),
                text=entry_texts,
                hovertemplate='%{x}<br>● 進場價: %{y:.2f}<br>%{text}<extra></extra>'
            ),
            row=1, col=1
        )

    # 部分出場點標記（橘色三角形）
    partial_exit_events = [e for e in events if e['type'] == 'partial_exit']
    if partial_exit_events:
        partial_times = [e['time'] for e in partial_exit_events]
        partial_prices = [e['price'] for e in partial_exit_events]

        # 收集 hover 文字
        partial_hover_texts = []
        for e in partial_exit_events:
            pnl = e.get('pnl_pct', 0)
            reason = e.get('reason', '')
            quantity = e.get('quantity', 0)
            partial_hover_texts.append(f"{reason}<br>出場: {quantity}張 (50%)<br>損益: {pnl:+.2f}%")

        fig.add_trace(
            go.Scatter(
                x=partial_times,
                y=partial_prices,
                mode='markers',
                name='部分出場 (50%)',
                marker=dict(
                    symbol='triangle-down',
                    size=16,
                    color='#FF9800',  # 橘色
                    line=dict(color='white', width=2)
                ),
                text=partial_hover_texts,
                hovertemplate='%{x}<br>▼ 部分出場價: %{y:.2f}<br>%{text}<extra></extra>'
            ),
            row=1, col=1
        )

    # 部位補回標記（藍色三角形向上）
    supplement_events = [e for e in events if e['type'] == 'supplement']
    if supplement_events:
        supplement_times = [e['time'] for e in supplement_events]
        supplement_prices = [e['price'] for e in supplement_events]

        # 收集 hover 文字
        supplement_hover_texts = []
        for e in supplement_events:
            quantity = e.get('quantity', 0)
            reason = e.get('reason', '')
            supplement_hover_texts.append(f"{reason}<br>補回: {quantity}張")

        fig.add_trace(
            go.Scatter(
                x=supplement_times,
                y=supplement_prices,
                mode='markers',
                name='部位補回',
                marker=dict(
                    symbol='triangle-up',
                    size=16,
                    color='#2196F3',  # 藍色
                    line=dict(color='white', width=2)
                ),
                text=supplement_hover_texts,
                hovertemplate='%{x}<br>▲ 補回價: %{y:.2f}<br>%{text}<extra></extra>'
            ),
            row=1, col=1
        )

    # 出場點標記（綠色下箭頭）
    exit_events = [e for e in events if e['type'] == 'exit']
    if exit_events:
        exit_times = [e['time'] for e in exit_events]
        exit_prices = [e['price'] for e in exit_events]

        # 收集 hover 文字
        hover_texts = []
        for e in exit_events:
            pnl = e.get('pnl_pct', 0)
            reason = e.get('exit_reason', '')

            reason_map = {
                'price_stop_loss': '價格停損',
                'momentum_stop_loss': '動能停損',
                'exhaustion': '力竭',
                'end_of_day': '收盤'
            }
            reason_text = reason_map.get(reason, reason)
            hover_texts.append(f"{reason_text} {pnl:+.2f}%")

        # 統一使用綠色圓點
        fig.add_trace(
            go.Scatter(
                x=exit_times,
                y=exit_prices,
                mode='markers',
                name='出場',
                marker=dict(
                    symbol='circle',
                    size=18,
                    color='#7FFF00',  # 黃綠色 (chartreuse)，非常明亮易見
                    line=dict(color='white', width=2)
                ),
                text=hover_texts,
                hovertemplate='%{x}<br>● 出場價: %{y:.2f}<br>%{text}<extra></extra>'
            ),
            row=1, col=1
        )

    # ========================================
    # Subplot 2: Ratio（顯示兩條線）
    # ========================================
    if has_ratio:
        ratio_data = ratio_data.copy()
        ratio_data['datetime'] = pd.to_datetime(ratio_data['datetime'])

        # 第一條線：ratio_15s_300s（無加權，進場條件用）
        if 'ratio_15s_300s' in ratio_data.columns:
            fig.add_trace(
                go.Scattergl(
                    x=ratio_data['datetime'],
                    y=ratio_data['ratio_15s_300s'].fillna(0),
                    mode='lines',
                    name='15s_300s (無加權)',
                    line=dict(color='#2196F3', width=1.5),
                    hovertemplate='%{x|%H:%M:%S}<br>15s_300s: %{y:.2f}<extra></extra>'
                ),
                row=2, col=1
            )

        # 第二條線：ratio_15s_180s_w321（加權）
        if 'ratio_15s_180s_w321' in ratio_data.columns:
            fig.add_trace(
                go.Scattergl(
                    x=ratio_data['datetime'],
                    y=ratio_data['ratio_15s_180s_w321'].fillna(0),
                    mode='lines',
                    name='15s_180s_w321 (加權)',
                    line=dict(color='#9C27B0', width=1, dash='dot'),
                    hovertemplate='%{x|%H:%M:%S}<br>15s_180s_w321: %{y:.2f}<extra></extra>'
                ),
                row=2, col=1
            )

        # 兼容舊格式（只有單一 ratio 欄位）
        if 'ratio' in ratio_data.columns and 'ratio_15s_300s' not in ratio_data.columns:
            # 嘗試找出實際的 ratio 欄位名稱（除了 'ratio' 和 'datetime'）
            ratio_columns = [col for col in ratio_data.columns if col not in ['datetime', 'ratio']]
            display_name = ratio_columns[0] if ratio_columns else 'Ratio'

            fig.add_trace(
                go.Scattergl(
                    x=ratio_data['datetime'],
                    y=ratio_data['ratio'].fillna(0),
                    mode='lines',
                    name=display_name,  # 顯示實際的欄位名稱
                    line=dict(color='#9C27B0', width=1),
                    hovertemplate=f'%{{x|%H:%M:%S}}<br>{display_name}: %{{y:.2f}}<extra></extra>'
                ),
                row=2, col=1
            )

        # Ratio = 1.0 參考線
        fig.add_hline(y=1.0, line_dash="dash", line_color="gray", row=2, col=1)

        # Ratio = 5.5 門檻線（進場/重置門檻）
        fig.add_hline(
            y=5.5,
            line_dash="dot",
            line_color="orange",
            annotation_text="門檻: 5.5",
            annotation_position="right",
            row=2, col=1
        )

        # 在 Ratio 圖上標記進場點
        if entry_events:
            entry_ratios = []
            for e in entry_events:
                entry_time = e['time']
                # 找到最接近的 ratio 值（優先使用 ratio_15s_300s）
                if len(ratio_data) > 0:
                    idx = (ratio_data['datetime'] - entry_time).abs().idxmin()
                    if 'ratio_15s_300s' in ratio_data.columns:
                        entry_ratios.append(ratio_data.loc[idx, 'ratio_15s_300s'])
                    elif 'ratio' in ratio_data.columns:
                        entry_ratios.append(ratio_data.loc[idx, 'ratio'])
                    else:
                        entry_ratios.append(0)
                else:
                    entry_ratios.append(0)

            fig.add_trace(
                go.Scatter(
                    x=entry_times,
                    y=entry_ratios,
                    mode='markers',
                    name='進場 (Ratio)',
                    marker=dict(
                        symbol='circle',
                        size=15,
                        color='red',
                        line=dict(color='white', width=1)
                    ),
                    hovertemplate='%{x}<br>進場 Ratio: %{y:.2f}<extra></extra>',
                    showlegend=False
                ),
                row=2, col=1
            )

        # 標記力竭出場的 peak_ratio 和 drawdown
        if exhaustion_info:
            for info in exhaustion_info:
                exit_time = info.get('time')
                peak_ratio = info.get('peak_ratio', 0)
                current_ratio = info.get('current_ratio', 0)
                drawdown = info.get('drawdown', 0)
                reset_count = info.get('reset_count', 0)

                # 標記 peak_ratio
                fig.add_trace(
                    go.Scatter(
                        x=[exit_time],
                        y=[peak_ratio],
                        mode='markers+text',
                        name='Peak Ratio',
                        marker=dict(
                            symbol='triangle-up',
                            size=10,
                            color='#E91E63',
                            line=dict(color='white', width=1)
                        ),
                        text=[f'峰值:{peak_ratio:.1f}'],
                        textposition='top center',
                        textfont=dict(size=9, color='#E91E63'),
                        hovertemplate=f'%{{x}}<br>峰值 Ratio: {peak_ratio:.2f}<br>回撤: {drawdown*100:.1f}%<br>重置次數: {reset_count}<extra></extra>',
                        showlegend=False
                    ),
                    row=2, col=1
                )

    else:
        # 沒有 ratio 資料時顯示提示
        fig.add_annotation(
            text="無 Ratio 資料",
            xref="paper", yref="paper",
            x=0.5, y=0.5,
            showarrow=False,
            font=dict(size=14, color="gray"),
            row=2, col=1
        )

    # ========================================
    # Subplot 3: 漲跌幅
    # ========================================
    if ref_price > 0:
        change_pct = (price_data['price'] - ref_price) / ref_price * 100

        fig.add_trace(
            go.Scattergl(
                x=price_data['datetime'],
                y=change_pct,
                mode='lines',
                name='漲跌幅',
                line=dict(color='#00BCD4', width=1),
                hovertemplate='%{x|%H:%M:%S}<br>漲跌幅: %{y:.2f}%<extra></extra>'
            ),
            row=3, col=1
        )

        # 零線
        fig.add_hline(y=0, line_dash="dash", line_color="gray", row=3, col=1)

        # 漲停線 (10%)
        fig.add_hline(
            y=10.0,
            line_dash="dot",
            line_color="red",
            annotation_text="漲停 10%",
            annotation_position="right",
            row=3, col=1
        )

    # ========================================
    # 更新佈局
    # ========================================
    fig.update_layout(
        title=dict(
            text=f"<b>{stock_id}</b> [{sector_name}]",
            font=dict(size=18)
        ),
        height=900,
        showlegend=True,
        legend=dict(
            orientation="h",
            yanchor="bottom",
            y=1.02,
            xanchor="right",
            x=1
        ),
        hovermode='x unified',
        template='plotly_white'
    )

    # 更新 X 軸格式
    fig.update_xaxes(tickformat='%H:%M:%S', title_text="時間", row=3, col=1)

    # 計算價格範圍（只考慮 Trade 數據，不包含 0）
    price_min = price_data['price'].min()
    price_max = price_data['price'].max()
    price_margin = (price_max - price_min) * 0.1  # 10% margin

    # 更新 Y 軸
    fig.update_yaxes(
        title_text="價格",
        row=1, col=1,
        range=[price_min - price_margin, price_max + price_margin]
    )
    fig.update_yaxes(title_text="Ratio", row=2, col=1)
    fig.update_yaxes(title_text="漲跌幅 (%)", row=3, col=1)

    return fig


def create_strategy_html(
    stock_id: str,
    sector_name: str,
    price_data: pd.DataFrame,
    events: List[Dict],
    ref_price: float,
    output_path: Path,
    ratio_data: pd.DataFrame = None,
    day_high_data: pd.DataFrame = None,
    exhaustion_info: List[Dict] = None
):
    """
    產生策略視覺化 HTML（使用 create_strategy_figure）

    Args:
        stock_id: 股票代碼
        sector_name: 族群名稱
        price_data: 價格資料 DataFrame (columns: datetime, price)
        events: 事件列表 (進場/出場)
        ref_price: 昨收價
        output_path: HTML 輸出路徑
        ratio_data: Ratio 資料 DataFrame (columns: datetime, ratio)
        day_high_data: Day High 資料 DataFrame (columns: datetime, day_high)
        exhaustion_info: 力竭出場資訊列表 (peak_ratio, drawdown, reset_count)
    """
    # 建立圖表
    fig = create_strategy_figure(
        stock_id=stock_id,
        sector_name=sector_name,
        price_data=price_data,
        events=events,
        ref_price=ref_price,
        ratio_data=ratio_data,
        day_high_data=day_high_data,
        exhaustion_info=exhaustion_info
    )

    if fig is None:
        logger.error("無法建立圖表")
        return

    # 確保輸出目錄存在
    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    # 產生交易摘要表格
    summary_html = _generate_trade_summary(events, ref_price, exhaustion_info)

    # 儲存 HTML
    html_content = fig.to_html(
        full_html=True,
        include_plotlyjs='cdn',
        config={'displayModeBar': True, 'scrollZoom': True}
    )

    # 在 </body> 前插入摘要表格
    html_content = html_content.replace('</body>', f'{summary_html}</body>')

    with open(output_path, 'w', encoding='utf-8') as f:
        f.write(html_content)

    logger.info(f"HTML 已儲存: {output_path}")


def _generate_trade_summary(events: List[Dict], ref_price: float, exhaustion_info: List[Dict] = None) -> str:
    """產生交易摘要 HTML 表格"""
    if not events:
        return ""

    entry_events = [e for e in events if e['type'] == 'entry']
    exit_events = [e for e in events if e['type'] == 'exit']
    partial_exit_events = [e for e in events if e['type'] == 'partial_exit']
    supplement_events = [e for e in events if e['type'] == 'supplement']

    total_pnl = sum(e.get('pnl_pct', 0) for e in exit_events)
    wins = len([e for e in exit_events if e.get('pnl_pct', 0) > 0])
    losses = len([e for e in exit_events if e.get('pnl_pct', 0) < 0])
    breakeven = len([e for e in exit_events if e.get('pnl_pct', 0) == 0])

    # 計算漲停價
    limit_up_price = calc_limit_up_price(ref_price)

    html = f"""
    <div style="margin: 20px; padding: 20px; background: #f5f5f5; border-radius: 8px; font-family: Arial, sans-serif;">
        <h3 style="margin-bottom: 15px;">交易摘要</h3>
        <table style="width: 100%; border-collapse: collapse;">
            <tr>
                <td style="padding: 8px; border-bottom: 1px solid #ddd;"><b>昨收價</b></td>
                <td style="padding: 8px; border-bottom: 1px solid #ddd;">{ref_price:.2f}</td>
                <td style="padding: 8px; border-bottom: 1px solid #ddd;"><b>漲停價</b></td>
                <td style="padding: 8px; border-bottom: 1px solid #ddd; color: red;">{limit_up_price:.2f}</td>
            </tr>
            <tr>
                <td style="padding: 8px; border-bottom: 1px solid #ddd;"><b>進場次數</b></td>
                <td style="padding: 8px; border-bottom: 1px solid #ddd;">{len(entry_events)}</td>
                <td style="padding: 8px; border-bottom: 1px solid #ddd;"><b>完全出場次數</b></td>
                <td style="padding: 8px; border-bottom: 1px solid #ddd;">{len(exit_events)}</td>
            </tr>
            <tr>
                <td style="padding: 8px; border-bottom: 1px solid #ddd;"><b>部分出場次數</b></td>
                <td style="padding: 8px; border-bottom: 1px solid #ddd; color: #FF9800;">{len(partial_exit_events)}</td>
                <td style="padding: 8px; border-bottom: 1px solid #ddd;"><b>部位補回次數</b></td>
                <td style="padding: 8px; border-bottom: 1px solid #ddd; color: #2196F3;">{len(supplement_events)}</td>
            </tr>
            <tr>
                <td style="padding: 8px; border-bottom: 1px solid #ddd;"><b>勝率</b></td>
                <td style="padding: 8px; border-bottom: 1px solid #ddd;">{wins}/{wins+losses+breakeven} ({(wins/(wins+losses+breakeven)*100) if wins+losses+breakeven > 0 else 0:.0f}%)</td>
                <td style="padding: 8px; border-bottom: 1px solid #ddd;"><b>獲利/平/虧</b></td>
                <td style="padding: 8px; border-bottom: 1px solid #ddd;">{wins} / {breakeven} / {losses}</td>
            </tr>
            <tr>
                <td style="padding: 8px;"><b>總損益</b></td>
                <td style="padding: 8px; color: {'green' if total_pnl >= 0 else 'red'}; font-weight: bold;" colspan="3">{total_pnl:+.2f}%</td>
            </tr>
        </table>
    """

    # 交易明細（含力竭資訊）
    if exit_events:
        html += """
        <h4 style="margin-top: 20px; margin-bottom: 10px;">交易明細</h4>
        <table style="width: 100%; border-collapse: collapse; font-size: 14px;">
            <tr style="background: #e0e0e0;">
                <th style="padding: 8px; text-align: left;">進場時間</th>
                <th style="padding: 8px; text-align: left;">出場時間</th>
                <th style="padding: 8px; text-align: right;">進場價</th>
                <th style="padding: 8px; text-align: right;">出場價</th>
                <th style="padding: 8px; text-align: left;">出場原因</th>
                <th style="padding: 8px; text-align: right;">Peak Ratio</th>
                <th style="padding: 8px; text-align: right;">Drawdown</th>
                <th style="padding: 8px; text-align: right;">損益</th>
            </tr>
        """

        for i, exit_e in enumerate(exit_events):
            entry_e = entry_events[i] if i < len(entry_events) else {}
            pnl = exit_e.get('pnl_pct', 0)
            pnl_color = 'green' if pnl > 0 else ('red' if pnl < 0 else 'gray')

            entry_time = entry_e.get('time', '')
            if hasattr(entry_time, 'strftime'):
                entry_time = entry_time.strftime('%H:%M:%S')

            exit_time = exit_e.get('time', '')
            if hasattr(exit_time, 'strftime'):
                exit_time = exit_time.strftime('%H:%M:%S')

            reason_map = {
                'price_stop_loss': '價格停損',
                'momentum_stop_loss': '動能停損',
                'exhaustion': '力竭',
                'end_of_day': '收盤'
            }
            reason = reason_map.get(exit_e.get('exit_reason', ''), exit_e.get('exit_reason', ''))

            # 取得力竭資訊
            peak_ratio = '-'
            drawdown = '-'
            if exhaustion_info and i < len(exhaustion_info):
                info = exhaustion_info[i]
                if info.get('peak_ratio'):
                    peak_ratio = f"{info['peak_ratio']:.2f}"
                if info.get('drawdown'):
                    drawdown = f"{info['drawdown']*100:.1f}%"

            html += f"""
            <tr style="border-bottom: 1px solid #ddd;">
                <td style="padding: 8px;">{entry_time}</td>
                <td style="padding: 8px;">{exit_time}</td>
                <td style="padding: 8px; text-align: right;">{entry_e.get('price', 0):.2f}</td>
                <td style="padding: 8px; text-align: right;">{exit_e.get('price', 0):.2f}</td>
                <td style="padding: 8px;">{reason}</td>
                <td style="padding: 8px; text-align: right; color: #9C27B0;">{peak_ratio}</td>
                <td style="padding: 8px; text-align: right; color: #E91E63;">{drawdown}</td>
                <td style="padding: 8px; text-align: right; color: {pnl_color}; font-weight: bold;">{pnl:+.2f}%</td>
            </tr>
            """

        html += "</table>"

    # 部分出場明細
    if partial_exit_events:
        html += """
        <h4 style="margin-top: 20px; margin-bottom: 10px;">部分出場明細</h4>
        <table style="width: 100%; border-collapse: collapse; font-size: 14px;">
            <tr style="background: #FFF3E0;">
                <th style="padding: 8px; text-align: left;">時間</th>
                <th style="padding: 8px; text-align: right;">價格</th>
                <th style="padding: 8px; text-align: left;">原因</th>
                <th style="padding: 8px; text-align: right;">出場數量</th>
                <th style="padding: 8px; text-align: right;">損益</th>
            </tr>
        """

        for e in partial_exit_events:
            pnl = e.get('pnl_pct', 0)
            pnl_color = 'green' if pnl > 0 else ('red' if pnl < 0 else 'gray')

            event_time = e.get('time', '')
            if hasattr(event_time, 'strftime'):
                event_time = event_time.strftime('%H:%M:%S')

            html += f"""
            <tr style="border-bottom: 1px solid #ddd;">
                <td style="padding: 8px;">{event_time}</td>
                <td style="padding: 8px; text-align: right;">{e.get('price', 0):.2f}</td>
                <td style="padding: 8px;">{e.get('reason', '')}</td>
                <td style="padding: 8px; text-align: right;">{e.get('quantity', 0)}張 (50%)</td>
                <td style="padding: 8px; text-align: right; color: {pnl_color}; font-weight: bold;">{pnl:+.2f}%</td>
            </tr>
            """

        html += "</table>"

    # 部位補回明細
    if supplement_events:
        html += """
        <h4 style="margin-top: 20px; margin-bottom: 10px;">部位補回明細</h4>
        <table style="width: 100%; border-collapse: collapse; font-size: 14px;">
            <tr style="background: #E3F2FD;">
                <th style="padding: 8px; text-align: left;">時間</th>
                <th style="padding: 8px; text-align: right;">價格</th>
                <th style="padding: 8px; text-align: left;">原因</th>
                <th style="padding: 8px; text-align: right;">補回數量</th>
            </tr>
        """

        for e in supplement_events:
            event_time = e.get('time', '')
            if hasattr(event_time, 'strftime'):
                event_time = event_time.strftime('%H:%M:%S')

            html += f"""
            <tr style="border-bottom: 1px solid #ddd;">
                <td style="padding: 8px;">{event_time}</td>
                <td style="padding: 8px; text-align: right;">{e.get('price', 0):.2f}</td>
                <td style="padding: 8px;">{e.get('reason', '')}</td>
                <td style="padding: 8px; text-align: right;">{e.get('quantity', 0)}張</td>
            </tr>
            """

        html += "</table>"

    html += "</div>"
    return html
