"""VWAP execution report generator (Adaptive 版).

Prints a formatted console report including adaptive strategy metrics.
"""

from __future__ import annotations

from strategy_modules.vwap_models import VWAPMetrics


class VWAPReportGenerator:
    """VWAP 執行報告生成器"""

    @staticmethod
    def print_report(metrics: VWAPMetrics, company_name: str = "") -> None:
        total_volume_quote = metrics.target_lots * metrics.start_price * 1000

        separator = "=========================================="

        print(separator)
        print(f"VWAP 執行報告 — {metrics.stock_id} {company_name}".rstrip())
        print(f"日期：{metrics.date}")
        print(separator)

        print()
        print("【執行摘要】")
        print(f"  目標金額:        {total_volume_quote:,.0f} TWD")
        print(f"  目標張數:        {metrics.target_lots:.2f} 張")
        print(f"  完成張數:        {metrics.total_filled_lots:.2f} 張")
        print(f"  完成率:          {metrics.completion_ratio:.1%}")
        print(f"  下單次數:        {metrics.total_orders} 次")
        print(f"  執行時間:        {metrics.execution_duration_minutes:.1f} 分鐘")

        print()
        print("【價格分析】")
        print(f"  起始價:          {metrics.start_price:.2f}")
        print(f"  執行 VWAP:       {metrics.vwap_price:.2f}")
        print(f"  市場 VWAP:       {metrics.market_vwap:.2f}")
        print(f"  滑價 (vs 起始):  {metrics.slippage_bps_vs_start:+.2f} bps")
        print(f"  滑價 (vs 市場):  {metrics.slippage_bps_vs_market:+.2f} bps")

        print()
        print("【市場影響】")
        print(f"  實際金額:        {metrics.total_spent_amount:,.0f} TWD")
        print(f"  參與率:          {metrics.participation_rate:.2%}")

        print()
        print("【策略分析 (Adaptive)】")
        print(f"  Taker 下單:      {metrics.taker_order_count} 次")
        print(f"  Maker 下單:      {metrics.maker_order_count} 次")
        taker_ratio = (
            metrics.taker_order_count / metrics.total_orders * 100
            if metrics.total_orders > 0 else 0
        )
        print(f"  Taker 比例:      {taker_ratio:.1f}%")
        print(f"  平均 MO ratio:   {metrics.avg_mo_ratio:.3f}")
        print(f"  量預測 R²:       {metrics.volume_prediction_r2:.4f}")
        print(separator)
