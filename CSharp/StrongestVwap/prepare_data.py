"""
Prepare static data CSV for the StrongestVwap C# backtest.
Generates prev_close, monthly avg trading value, etc. from close.parquet + screening_results.csv.

Usage:
    python prepare_data.py --date 2026-03-06
    python prepare_data.py --date 2026-03-06 --output D:\\回測結果\\ModeE_test
"""
import argparse
import os
import sys

import pandas as pd


SCREENING_CSV = r"C:\Users\User\Documents\_02_bt\Backtest_tick_module\screening_results.csv"
CLOSE_PARQUET = r"D:\03_預估量相關資量\CSharp\close.parquet"
TICK_BASE = r"D:\feature_data\feature"


def main():
    parser = argparse.ArgumentParser(description="Prepare static data for C# backtest")
    parser.add_argument("--date", required=True, help="Target date (YYYY-MM-DD)")
    parser.add_argument("--screening_csv", default=SCREENING_CSV)
    parser.add_argument("--close_parquet", default=CLOSE_PARQUET)
    parser.add_argument("--tick_base", default=TICK_BASE)
    parser.add_argument("--output", default=None, help="Output directory (default: tick_base/date/)")
    args = parser.parse_args()

    target_date = args.date
    out_dir = args.output or os.path.join(args.tick_base, target_date)
    os.makedirs(out_dir, exist_ok=True)

    # 1. Load screening results for target date
    screening = pd.read_csv(args.screening_csv)
    day_records = screening[screening["date"] == target_date].copy()
    if len(day_records) == 0:
        print(f"[ERROR] No screening records for {target_date}")
        sys.exit(1)

    stock_ids = day_records["stock_id"].astype(str).unique().tolist()
    print(f"[PREP] {len(stock_ids)} stocks from screening for {target_date}")

    # 2. Load prev close from close.parquet
    close_df = pd.read_parquet(args.close_parquet)
    target_ts = pd.Timestamp(target_date)
    prev_dates = close_df.index[close_df.index < target_ts]
    if len(prev_dates) == 0:
        print("[ERROR] No previous trading dates found")
        sys.exit(1)
    prev_date = prev_dates[-1]
    print(f"[PREP] Previous trading day: {prev_date.strftime('%Y-%m-%d')}")

    # 3. Build static data
    rows = []
    for sid in stock_ids:
        sid_str = str(sid)
        prev_close = float(close_df.loc[prev_date, sid_str]) if sid_str in close_df.columns else 0.0

        # Get avg_amount_20d from screening
        rec = day_records[day_records["stock_id"].astype(str) == sid_str]
        avg_amount_20d = float(rec["avg_amount_20d"].iloc[0]) if len(rec) > 0 else 0.0

        rows.append({
            "stock_id": sid_str,
            "previous_close": prev_close,
            "monthly_avg_trading_value": avg_amount_20d,
            "security_type": "",  # Will be populated from tick data if available
            "prev_day_limit_up": False,
            "prev_close_0050": 0.0,
        })

    static_df = pd.DataFrame(rows)
    static_csv_path = os.path.join(out_dir, "static_data.csv")
    static_df.to_csv(static_csv_path, index=False)
    print(f"[PREP] Written {static_csv_path} ({len(rows)} stocks)")

    # 4. Check tick data availability
    available = 0
    for sid in stock_ids:
        tick_path = os.path.join(args.tick_base, target_date, f"{sid}.parquet")
        if os.path.exists(tick_path):
            available += 1
    print(f"[PREP] Tick data available: {available}/{len(stock_ids)}")

    print(f"\n[PREP] Done. Run C# backtest with:")
    print(f"  dotnet run -- --mode single --date {target_date} \\")
    print(f"    --config configs/mode_e_group_screening.yaml \\")
    print(f"    --screening_csv \"{args.screening_csv}\" \\")
    print(f"    --tick_data \"{args.tick_base}\" \\")
    print(f"    --static_data \"{static_csv_path}\" \\")
    print(f"    --output_path \"{out_dir}\"")


if __name__ == "__main__":
    main()
