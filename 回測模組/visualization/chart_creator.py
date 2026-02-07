"""



視覺化圖表建立模組



負責產生進出場策略的互動式圖表



"""







import os



import logging



import pandas as pd



import numpy as np



import plotly.graph_objects as go



import plotly.io as pio



from plotly.subplots import make_subplots



from typing import Dict, List, Optional



import importlib.util







logger = logging.getLogger(__name__)











class ChartCreator:



    """圖表建立器"""







    def __init__(self, config: Optional[Dict] = None):



        """



        初始化圖表建立器







        Args:



            config: 設定字典



        """



        self.config = config or {}







    def create_strategy_chart(



        self,



        df: pd.DataFrame,



        trades: List[Dict],



        output_path: str,



        png_output_path: Optional[str] = None,



        ref_price: Optional[float] = None,



        limit_up_price: Optional[float] = None,



        stock_id: Optional[str] = None,



        company_name: Optional[str] = None



    ) -> str:



        """



        建立策略視覺化圖表







        Args:



            df: 資料 DataFrame



            trades: 交易記錄列表



            output_path: HTML 輸出路徑



            png_output_path: PNG 輸出路徑（可選）



            ref_price: 參考價格（昨收）



            limit_up_price: 漲停價



            stock_id: 股票代碼



            company_name: 公司名稱







        Returns:



            輸出路徑



        """



        # 過濾掉價格為 0 的資料（五檔資料）



        # 保留原始 df 供其他指標使用，價格相關圖表使用過濾後的資料



        df_price = df[df['price'] > 0].copy() if 'price' in df.columns else df.copy()







        # 如果過濾後資料太少，使用原始資料



        if len(df_price) < 10:



            logger.warning(f"過濾後資料筆數過少 ({len(df_price)} 筆)，使用原始資料")



            df_price = df.copy()







        ratio_column = self.config.get('ratio_column', 'ratio_15s_300s')



        if ratio_column not in df.columns:



            ratio_column = 'ratio_15s_300s'







        # 建立子圖



        # 使用 shared_xaxes='columns' 以允許第一個子圖顯示時間軸



        fig = make_subplots(



            rows=5, cols=1,



            shared_xaxes='columns',



            vertical_spacing=0.03,  # 減少間距以容納更多子圖



            row_heights=[0.34, 0.18, 0.18, 0.15, 0.15],  # 調整高度比例，價格圖最大



            subplot_titles=(



                'Day High Break + Signals',



                'Ratio',



                'Day High Growth Rate',



                'Orderbook Thickness',



                'Balance Ratio'



            )



        )







        # 子圖1：價格與進出場點（調整為第一個）



        # 使用過濾後的資料顯示價格



        self._add_price_chart(fig, df_price, trades, row=1, col=1, ref_price=ref_price, limit_up_price=limit_up_price)







        # 子圖4：Ratio 指標



        self._add_ratio_chart(fig, df, ratio_column, row=2, col=1)







        # 子圖5：Day High 增長率



        self._add_growth_rate_chart(fig, df, row=3, col=1)







        # 子圖6：掛單厚度



        self._add_orderbook_thickness_chart(fig, df, row=4, col=1)







        # 子圖7：掛單平衡度



        self._add_balance_ratio_chart(fig, df, row=5, col=1)







        # 更新佈局



        self._update_layout(fig, stock_id=stock_id, company_name=company_name)







        # 儲存檔案



        self._save_chart(fig, output_path, png_output_path)







        return output_path







    def _add_price_chart(self, fig, df, trades, row, col, ref_price=None, limit_up_price=None):



        """添加價格圖表"""



        # 確保只使用有效的價格資料（過濾掉 price = 0 和 day_high = 0 的資料）



        valid_df = df[(df['price'] > 0) & (df['day_high'] > 0)].copy() if 'day_high' in df.columns else df[df['price'] > 0].copy()







        if len(valid_df) == 0:



            logger.warning("沒有有效的價格資料可顯示")



            return







        # 計算 Y 軸範圍



        price_min = valid_df['price'].min()



        price_max = valid_df['price'].max()



        if 'day_high' in valid_df.columns:



            price_max = max(price_max, valid_df['day_high'].max())







        # 增加一些邊距（5%）



        price_range = price_max - price_min



        y_min = max(0, price_min - price_range * 0.05)



        y_max = price_max + price_range * 0.05







        # 價格線



        fig.add_trace(



            go.Scatter(



                x=valid_df['time'], y=valid_df['price'],



                mode='lines',



                name='價格',



                line=dict(color='black', width=1),



                hovertemplate='價格: %{y:.2f}<br>時間: %{x}'



            ),



            row=row, col=col



        )







        # Day High 線



        if 'day_high' in valid_df.columns:



            fig.add_trace(



                go.Scatter(



                    x=valid_df['time'], y=valid_df['day_high'],



                    mode='lines',



                    name='Day High',



                    line=dict(color='red', width=2),



                    opacity=0.7,



                    hovertemplate='Day High: %{y:.2f}<br>時間: %{x}'



                ),



                row=row, col=col



            )







        # 設定 Y 軸範圍



        fig.update_yaxes(range=[y_min, y_max], row=row, col=col)







        # 昨收線（參考價格）



        if ref_price is not None and ref_price > 0:



            fig.add_trace(



                go.Scatter(



                    x=[df['time'].iloc[0], df['time'].iloc[-1]],



                    y=[ref_price, ref_price],



                    mode='lines',



                    name='昨收',



                    line=dict(color='blue', width=1.5, dash='dash'),



                    hovertemplate=f'昨收: {ref_price:.2f}<br>時間: %{{x}}'



                ),



                row=row, col=col



            )







        # 漲停線



        if limit_up_price is not None and limit_up_price > 0:



            fig.add_trace(



                go.Scatter(



                    x=[df['time'].iloc[0], df['time'].iloc[-1]],



                    y=[limit_up_price, limit_up_price],



                    mode='lines',



                    name='漲停價',



                    line=dict(color='red', width=1.5, dash='dot'),



                    hovertemplate=f'漲停價: {limit_up_price:.2f}<br>時間: %{{x}}'



                ),



                row=row, col=col



            )







        # 添加移動停利低點線（1分鐘、3分鐘、5分鐘）



        # 1分鐘低點線



        if 'low_1m' in df.columns:



            fig.add_trace(



                go.Scatter(



                    x=df['time'], y=df['low_1m'],



                    mode='lines',



                    name='1分鐘低點',



                    line=dict(color='green', width=1, dash='dash'),



                    opacity=0.6,



                    hovertemplate='1分鐘低點: %{y:.2f}<br>時間: %{x}'



                ),



                row=row, col=col



            )







        # 3分鐘低點線



        if 'low_3m' in df.columns:



            fig.add_trace(



                go.Scatter(



                    x=df['time'], y=df['low_3m'],



                    mode='lines',



                    name='3分鐘低點',



                    line=dict(color='orange', width=1, dash='dash'),



                    opacity=0.6,



                    hovertemplate='3分鐘低點: %{y:.2f}<br>時間: %{x}'



                ),



                row=row, col=col



            )







        # 5分鐘低點線



        if 'low_5m' in df.columns:



            fig.add_trace(



                go.Scatter(



                    x=df['time'], y=df['low_5m'],



                    mode='lines',



                    name='5分鐘低點',



                    line=dict(color='purple', width=1, dash='dash'),



                    opacity=0.6,



                    hovertemplate='5分鐘低點: %{y:.2f}<br>時間: %{x}'



                ),



                row=row, col=col



            )







        # 標記進出場點



        for i, trade in enumerate(trades):



            # 進場點



            fig.add_trace(



                go.Scatter(



                    x=[trade['entry_time']], y=[trade['entry_price']],



                    mode='markers',



                    name='進場' if i == 0 else None,



                    showlegend=(i == 0),



                    marker=dict(color='red', size=15, symbol='circle'),



                    hovertemplate=f"進場<br>價格: {trade['entry_price']:.2f}<br>Ratio: {trade.get('entry_ratio', 0):.1f}<br>時間: %{{x}}"



                ),



                row=row, col=col



            )







            # 判斷是否為移動停利模式（檢查是否有移動停利出場記錄）



            has_trailing_exits = trade.get('trailing_exit_details') and len(trade.get('trailing_exit_details', [])) > 0







            if has_trailing_exits:



                # 移動停利模式：標記所有出場點



                exit_colors = {'1min': 'green', '3min': 'orange', '5min': 'purple'}



                for j, exit_detail in enumerate(trade['trailing_exit_details']):



                    level_name = exit_detail['level']



                    exit_color = exit_colors.get(level_name, 'gray')







                    fig.add_trace(



                        go.Scatter(



                            x=[exit_detail['time']], y=[exit_detail['price']],



                            mode='markers',



                            name=f"移動停利 {level_name}" if i == 0 else None,



                            showlegend=(i == 0),



                            marker=dict(color=exit_color, size=15, symbol='triangle-down'),



                            hovertemplate=f"移動停利出場 ({level_name})<br>價格: {exit_detail['price']:.2f}<br>出場比例: {exit_detail['ratio']:.1%}<br>時間: %{{x}}"



                        ),



                        row=row, col=col



                    )







                # 如果有最終出場，額外標記（但如果已經完全出場就不需要）
                # 計算已出場比例
                total_exit_ratio = sum(exit['ratio'] for exit in trade.get('trailing_exit_details', []))

                # 只有當還有剩餘部位時才標記最終出場
                if trade.get('final_exit_time') and total_exit_ratio < 0.99:



                    final_reason = trade.get('final_exit_reason', '')



                    final_label = '進場價保護' if '進場價保護' in final_reason else '清倉'



                    hover_text = f"{final_label}<br>價格: {trade['final_exit_price']:.2f}<br>時間: %{{x}}"



                    if final_reason:



                        hover_text = f"{final_label} ({final_reason})<br>價格: {trade['final_exit_price']:.2f}<br>時間: %{{x}}"







                    fig.add_trace(



                        go.Scatter(



                            x=[trade['final_exit_time']], y=[trade['final_exit_price']],



                            mode='markers',



                            name=final_label if i == 0 else None,



                            showlegend=(i == 0),



                            marker=dict(color='green', size=15, symbol='circle'),



                            hovertemplate=hover_text



                        ),



                        row=row, col=col



                    )



            elif trade.get('partial_exit_time') and trade.get('partial_exit_reason', '').find('漲停') >= 0:



                # 如果是漲停價出場（只標記漲停出場點）



                fig.add_trace(



                    go.Scatter(



                        x=[trade['partial_exit_time']], y=[trade['partial_exit_price']],



                        mode='markers',



                        name='漲停價出場' if i == 0 else None,



                        showlegend=(i == 0),



                        marker=dict(color='orange', size=15, symbol='triangle-down'),



                        hovertemplate=f"漲停價出場（50%）<br>價格: {trade['partial_exit_price']:.2f}<br>時間: %{{x}}"



                    ),



                    row=row, col=col



                )



            else:



                # 原始邏輯：顯示所有出場點



                # 第一階段出場



                if trade.get('partial_exit_time'):



                    fig.add_trace(



                        go.Scatter(



                            x=[trade['partial_exit_time']], y=[trade['partial_exit_price']],



                            mode='markers',



                            name='減碼50%' if i == 0 else None,



                            showlegend=(i == 0),



                            marker=dict(color='orange', size=15, symbol='triangle-down'),



                            hovertemplate=f"減碼50%<br>價格: {trade['partial_exit_price']:.2f}<br>時間: %{{x}}"



                        ),



                        row=row, col=col



                    )







                # 回補進場



                if trade.get('reentry_time'):



                    fig.add_trace(



                        go.Scatter(



                            x=[trade['reentry_time']], y=[trade['reentry_price']],



                            mode='markers',



                            name='回補進場' if i == 0 else None,



                            showlegend=(i == 0),



                            marker=dict(color='blue', size=15, symbol='triangle-up'),



                            hovertemplate=f"回補進場<br>價格: {trade['reentry_price']:.2f}<br>時間: %{{x}}"



                        ),



                        row=row, col=col



                    )







                # 最終出場



                if trade.get('final_exit_time'):



                    is_reentry_exit = trade.get('reentry_exit_reason') is not None



                    exit_color = 'green'  # 統一使用綠色



                    exit_symbol = 'circle'  # 統一使用圓點



                    exit_label = '回補停損' if is_reentry_exit else '清倉'



                    exit_reason = trade.get('reentry_exit_reason')



                    hover_text = f"{exit_label}<br>價格: {trade['final_exit_price']:.2f}<br>時間: %{{x}}"



                    if exit_reason:



                        hover_text = f"{exit_label} ({exit_reason})<br>價格: {trade['final_exit_price']:.2f}<br>時間: %{{x}}"







                    fig.add_trace(



                        go.Scatter(



                            x=[trade['final_exit_time']], y=[trade['final_exit_price']],



                            mode='markers',



                            name=exit_label if i == 0 else None,



                            showlegend=(i == 0),



                            marker=dict(color=exit_color, size=15, symbol=exit_symbol),



                            hovertemplate=hover_text



                        ),



                        row=row, col=col



                    )







    def _add_ratio_chart(self, fig, df, ratio_column, row, col):



        """添加 Ratio 指標圖表"""



        fig.add_trace(



            go.Scatter(



                x=df['time'], y=df[ratio_column],



                mode='lines',



                name=f"Ratio ({ratio_column})",



                line=dict(color='purple', width=1.5),



                hovertemplate='Ratio: %{y:.2f}<br>時間: %{x}'



            ),



            row=row, col=col



        )







        # 進場門檻線



        ratio_threshold = self.config.get('ratio_entry_threshold', 3.0)



        fig.add_hline(



            y=ratio_threshold, line_dash="dash", line_color="red",



            annotation_text=f"進場門檻 ({ratio_threshold:.1f})",



            row=row, col=col



        )







        # 可進場區間



        ratio_high = df[ratio_column].copy()



        ratio_high[ratio_high < ratio_threshold] = np.nan







        fig.add_trace(



            go.Scatter(



                x=df['time'], y=ratio_high,



                fill='tozeroy',



                mode='none',



                name='可進場區間',



                fillcolor='rgba(0, 255, 0, 0.2)',



                showlegend=True



            ),



            row=row, col=col



        )







    def _add_growth_rate_chart(self, fig, df, row, col):



        """添加 Day High 增長率圖表"""



        fig.add_trace(



            go.Scatter(



                x=df['time'], y=df['day_high_growth_rate'] * 100,



                mode='lines',



                name='DH增長率(%)',



                line=dict(color='blue', width=1),



                hovertemplate='DH增長率: %{y:.2f}%<br>時間: %{x}'



            ),



            row=row, col=col



        )







        # 0.86% 門檻線



        fig.add_hline(



            y=0.86, line_dash="dash", line_color="red",



            annotation_text="0.86%門檻",



            row=row, col=col



        )







        # 動能衰竭區域



        growth_low = df['day_high_growth_rate'] * 100



        growth_low_fill = growth_low.copy()



        growth_low_fill[growth_low_fill >= 0.86] = np.nan



        fig.add_trace(



            go.Scatter(



                x=df['time'], y=growth_low_fill,



                fill='tozeroy',



                mode='none',



                name='動能衰竭',



                fillcolor='rgba(255, 0, 0, 0.2)',



                showlegend=True



            ),



            row=row, col=col



        )







    def _add_orderbook_thickness_chart(self, fig, df, row, col):



        """添加掛單厚度圖表"""



        fig.add_trace(



            go.Scatter(



                x=df['time'], y=df['bid_avg_volume'],



                mode='lines',



                name='買方平均掛單量',



                line=dict(color='green', width=1.5),



                hovertemplate='買方掛單: %{y:.0f}<br>時間: %{x}'



            ),



            row=row, col=col



        )







        fig.add_trace(



            go.Scatter(



                x=df['time'], y=df['ask_avg_volume'],



                mode='lines',



                name='賣方平均掛單量',



                line=dict(color='red', width=1.5),



                hovertemplate='賣方掛單: %{y:.0f}<br>時間: %{x}'



            ),



            row=row, col=col



        )







        # 門檻線



        fig.add_hline(y=20, line_dash="dash", line_color="gray",



                      annotation_text="薄掛單門檻", row=row, col=col)



        fig.add_hline(y=40, line_dash="solid", line_color="gray",



                      annotation_text="正常掛單門檻", row=row, col=col)







    def _add_balance_ratio_chart(self, fig, df, row, col):



        """添加掛單平衡度圖表"""



        fig.add_trace(



            go.Scatter(



                x=df['time'], y=df['balance_ratio'],



                mode='lines',



                name='買/賣掛單比',



                line=dict(color='purple', width=1.5),



                hovertemplate='掛單比: %{y:.2f}<br>時間: %{x}'



            ),



            row=row, col=col



        )







        # 平衡線



        fig.add_hline(y=1.0, line_dash="solid", line_color="black",



                      annotation_text="完全平衡", row=row, col=col)







        # 平衡區間



        fig.add_hrect(y0=0.8, y1=1.2,



                      fillcolor="rgba(0, 255, 0, 0.1)",



                      annotation_text="平衡區間",



                      row=row, col=col)







    def _add_all_orders_io_ratio_chart(self, fig, df, row, col):



        """添加所有單內外盤比圖表"""



        # 檢查欄位是否存在



        if 'outside_ratio' not in df.columns:



            logger.warning("DataFrame 中沒有 'outside_ratio' 欄位，跳過所有單內外盤比圖表")



            return







        # 外盤佔比



        fig.add_trace(



            go.Scatter(



                x=df['time'],



                y=df['outside_ratio'] * 100,  # 轉換為百分比



                mode='lines',



                name='外盤佔比(%)',



                line=dict(color='red', width=2),



                hovertemplate='外盤佔比: %{y:.1f}%<br>時間: %{x}'



            ),



            row=row, col=col



        )







        # 內盤佔比



        fig.add_trace(



            go.Scatter(



                x=df['time'],



                y=(1 - df['outside_ratio']) * 100,  # 內盤佔比 = 100% - 外盤佔比



                mode='lines',



                name='內盤佔比(%)',



                line=dict(color='green', width=2),



                hovertemplate='內盤佔比: %{y:.1f}%<br>時間: %{x}'



            ),



            row=row, col=col



        )







        # 50% 平衡線



        fig.add_hline(



            y=50,



            line_dash="solid",



            line_color="gray",



            line_width=1.5,



            annotation_text="50%",



            row=row, col=col



        )







        # 強勢區域



        fig.add_hrect(



            y0=60, y1=100,



            fillcolor="rgba(255, 0, 0, 0.05)",



            annotation_text="買盤強勢",



            row=row, col=col



        )







        fig.add_hrect(



            y0=0, y1=40,



            fillcolor="rgba(0, 255, 0, 0.05)",



            annotation_text="賣盤強勢",



            row=row, col=col



        )







    def _add_large_orders_io_ratio_chart(self, fig, df, row, col):



        """添加大單內外盤比圖表"""



        # 檢查欄位是否存在



        if 'large_order_outside_ratio' not in df.columns:



            logger.warning("DataFrame 中沒有 'large_order_outside_ratio' 欄位，跳過大單內外盤比圖表")



            return







        # 大單外盤佔比



        fig.add_trace(



            go.Scatter(



                x=df['time'],



                y=df['large_order_outside_ratio'] * 100,  # 轉換為百分比



                mode='lines',



                name='大單外盤佔比(%)',



                line=dict(color='darkred', width=2.5),



                hovertemplate='大單外盤佔比: %{y:.1f}%<br>時間: %{x}'



            ),



            row=row, col=col



        )







        # 大單內盤佔比



        fig.add_trace(



            go.Scatter(



                x=df['time'],



                y=(1 - df['large_order_outside_ratio']) * 100,  # 內盤佔比 = 100% - 外盤佔比



                mode='lines',



                name='大單內盤佔比(%)',



                line=dict(color='darkgreen', width=2.5),



                hovertemplate='大單內盤佔比: %{y:.1f}%<br>時間: %{x}'



            ),



            row=row, col=col



        )







        # 50% 平衡線



        fig.add_hline(



            y=50,



            line_dash="solid",



            line_color="black",



            line_width=2,



            annotation_text="50%",



            row=row, col=col



        )







        # 強勢區域



        fig.add_hrect(



            y0=70, y1=100,



            fillcolor="rgba(139, 0, 0, 0.08)",



            annotation_text="大單買盤強勢",



            row=row, col=col



        )







        fig.add_hrect(



            y0=0, y1=30,



            fillcolor="rgba(0, 100, 0, 0.08)",



            annotation_text="大單賣盤強勢",



            row=row, col=col



        )











    def _update_layout(self, fig, stock_id=None, company_name=None):



        """更新圖表佈局"""



        # 建立標題



        if stock_id and company_name:



            title_text = f"{stock_id} {company_name} - Day High 突破進場 + 動能衰竭出場策略"



        elif stock_id:



            title_text = f"{stock_id} - Day High 突破進場 + 動能衰竭出場策略"



        else:



            title_text = "Day High 突破進場 + 動能衰竭出場策略分析"







        fig.update_layout(



            title_text=title_text,



            title_font_size=18,



            title_x=0.02,  # 標題靠左對齊



            showlegend=True,



            height=1200,  # 增加高度以容納7個子圖



            hovermode='x unified',



            xaxis_rangeslider_visible=False,



            plot_bgcolor='white',



            paper_bgcolor='white'



        )







        # 更新 x 軸設定



        # 在第一個子圖下方顯示時間軸標籤



        fig.update_xaxes(title_text="時間", row=1, col=1, showticklabels=True)



        # 其他子圖隱藏 x 軸標籤但保留網格



        for row_idx in range(2, 6):



            fig.update_xaxes(showticklabels=False, row=row_idx, col=1)



        # 最後一個子圖顯示 x 軸標籤（備用）



        fig.update_xaxes(showticklabels=True, row=5, col=1)



        fig.update_xaxes(showgrid=True, gridwidth=1, gridcolor='lightgray')







        # 更新 y 軸設定



        fig.update_yaxes(title_text="Price", row=1, col=1)

        fig.update_yaxes(title_text="Ratio", row=2, col=1)

        fig.update_yaxes(title_text="Day High Growth Rate", row=3, col=1)

        fig.update_yaxes(title_text="Orderbook Thickness", row=4, col=1)

        fig.update_yaxes(title_text="Balance Ratio", row=5, col=1)

        fig.update_yaxes(showgrid=True, gridwidth=1, gridcolor='lightgray')







    def _save_chart(self, fig, output_path, png_output_path=None):



        """儲存圖表"""



        # 儲存為 HTML



        fig.write_html(



            output_path,



            config={'displayModeBar': True, 'scrollZoom': True}



        )



        logger.info(f"互動式圖表已儲存至: {output_path}")







        # 嘗試儲存為 PNG



        if png_output_path:



            if importlib.util.find_spec('kaleido') is None:



                logger.warning("PNG 生成失敗: kaleido 未安裝")



            else:



                try:



                    pio.write_image(fig, png_output_path, width=1920, height=1080)



                    logger.info(f"PNG 圖片已儲存至: {png_output_path}")



                except Exception as exc:



                    logger.warning(f"PNG 生成失敗: {exc}")











def create_exit_visualization(



    df: pd.DataFrame,



    trades: List[Dict],



    output_path: str,



    config: Optional[Dict] = None,



    png_output_path: Optional[str] = None



) -> str:



    """



    建立進出場視覺化圖表（相容性函數）







    Args:



        df: 資料 DataFrame



        trades: 交易記錄列表



        output_path: HTML 輸出路徑



        config: 設定字典



        png_output_path: PNG 輸出路徑







    Returns:



        輸出路徑



    """



    creator = ChartCreator(config)



    return creator.create_strategy_chart(df, trades, output_path, png_output_path)



