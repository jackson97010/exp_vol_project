"""
Generate static_data.csv for all feature_data dates that are missing it.

Fields:
  stock_id            - from screening_results.csv (stocks in that date)
  previous_close      - from close.parquet (previous trading day's close)
  monthly_avg_trading_value - from screening_results.csv (avg_amount_20d)
  security_type       - "RR" for 處置股, else empty (from existing static_data or hardcoded list)
  prev_day_limit_up   - True if previous day close >= limit-up price (10% of day before)
  prev_close_0050     - 0050.TW previous close (from close.parquet if available)
"""

import pandas as pd
import numpy as np
import os
import sys
from datetime import datetime

# === Config ===
FEATURE_BASE = r"D:\feature_data\feature"
CLOSE_PATH = r"D:\03_預估量相關資量\回測模組\close.parquet"
SCREENING_PATH = r"C:\Users\User\Documents\_02_bt\Backtest_tick_module\screening_results.csv"

# === Load data ===
print("Loading close.parquet...")
close_df = pd.read_parquet(CLOSE_PATH)
close_df.index = pd.to_datetime(close_df.index)
print(f"  Close: {close_df.index[0].date()} ~ {close_df.index[-1].date()}, {len(close_df.columns)} stocks")

print("Loading screening_results.csv...")
screening = pd.read_csv(SCREENING_PATH)
screening['date'] = screening['date'].astype(str)
print(f"  Screening: {len(screening)} rows, {screening['date'].nunique()} dates")

# Build date -> previous trading date mapping from close_df
close_dates = sorted(close_df.index)
prev_date_map = {}
for i, d in enumerate(close_dates):
    if i > 0:
        prev_date_map[d] = close_dates[i - 1]

# For limit-up detection: need close 2 days before
prev2_date_map = {}
for i, d in enumerate(close_dates):
    if i > 1:
        prev2_date_map[d] = close_dates[i - 2]


def calc_limit_up_price(prev_close):
    """Calculate limit-up price (10% rounded to tick size)."""
    if prev_close <= 0 or np.isnan(prev_close):
        return 0
    raw = prev_close * 1.10
    # Tick size rules for TWSE
    if raw < 10:
        return np.floor(raw * 100) / 100  # 0.01
    elif raw < 50:
        return np.floor(raw * 20) / 20    # 0.05
    elif raw < 100:
        return np.floor(raw * 10) / 10    # 0.1
    elif raw < 500:
        return np.floor(raw * 2) / 2      # 0.5
    elif raw < 1000:
        return np.floor(raw)               # 1
    else:
        return np.floor(raw / 5) * 5       # 5


# === Process each date ===
feature_dates = sorted([d for d in os.listdir(FEATURE_BASE) if d.startswith("20")])
generated = 0
skipped = 0

for date_str in feature_dates:
    csv_path = os.path.join(FEATURE_BASE, date_str, "static_data.csv")
    if os.path.exists(csv_path):
        skipped += 1
        continue

    dt = pd.Timestamp(date_str)

    # Get previous trading date
    if dt not in prev_date_map:
        # dt might not be in close_df index — find closest previous
        prev_dates = [d for d in close_dates if d < dt]
        if not prev_dates:
            print(f"  {date_str}: No previous close date available, skipping")
            continue
        prev_dt = prev_dates[-1]
    else:
        prev_dt = prev_date_map[dt]

    # Get 2-day-ago close for limit-up detection
    prev2_dates = [d for d in close_dates if d < prev_dt]
    prev2_dt = prev2_dates[-1] if prev2_dates else None

    # Get stocks from screening for this date
    day_screening = screening[screening['date'] == date_str]

    # Also get stocks from parquet files in the directory
    parquet_stocks = set()
    day_dir = os.path.join(FEATURE_BASE, date_str)
    for f in os.listdir(day_dir):
        if f.endswith('.parquet') and f != 'static_data.parquet':
            sid = f.replace('.parquet', '')
            parquet_stocks.add(sid)

    # Merge: use screening stocks (they have avg_amount_20d)
    screening_stocks = {}
    for _, row in day_screening.iterrows():
        sid = str(row['stock_id'])
        screening_stocks[sid] = row.get('avg_amount_20d', 0)

    # All stocks = screening stocks that also have parquet data
    all_stocks = set(screening_stocks.keys()) & parquet_stocks

    if not all_stocks:
        # If no screening data for this date, use all parquet stocks
        all_stocks = parquet_stocks

    rows = []
    for sid in sorted(all_stocks):
        prev_close = 0.0
        if sid in close_df.columns and prev_dt is not None:
            val = close_df.loc[prev_dt, sid] if prev_dt in close_df.index else np.nan
            if not np.isnan(val):
                prev_close = val

        monthly_avg = screening_stocks.get(sid, 0)

        # Security type: we don't have a reliable source, leave empty
        # (the screening CSV already filters appropriately)
        security_type = ""

        # Prev day limit up: check if previous close >= limit-up price of 2 days ago
        prev_day_limit_up = False
        if prev2_dt is not None and sid in close_df.columns:
            close_2ago = close_df.loc[prev2_dt, sid] if prev2_dt in close_df.index else np.nan
            if not np.isnan(close_2ago) and close_2ago > 0:
                limit_up = calc_limit_up_price(close_2ago)
                if prev_close >= limit_up and limit_up > 0:
                    prev_day_limit_up = True

        # 0050 previous close
        prev_close_0050 = 0.0
        if '0050' in close_df.columns and prev_dt in close_df.index:
            val_0050 = close_df.loc[prev_dt, '0050']
            if not np.isnan(val_0050):
                prev_close_0050 = val_0050

        rows.append({
            'stock_id': sid,
            'previous_close': prev_close,
            'monthly_avg_trading_value': monthly_avg,
            'security_type': security_type,
            'prev_day_limit_up': prev_day_limit_up,
            'prev_close_0050': prev_close_0050,
        })

    if rows:
        df_out = pd.DataFrame(rows)
        df_out.to_csv(csv_path, index=False)
        generated += 1
        if generated <= 5 or generated % 50 == 0:
            print(f"  {date_str}: Generated {len(rows)} stocks (prev_close from {prev_dt.date()})")
    else:
        print(f"  {date_str}: No stocks to write")

print(f"\nDone! Generated: {generated}, Already existed: {skipped}")
