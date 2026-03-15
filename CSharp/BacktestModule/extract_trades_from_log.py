"""
Extract all trades from batch backtest log → per-variant CSV files.

Parses [OPEN]/[CLOSE] pairs, tracks variant/date/stock context.
Output: D:\backtest_compare\{variant}_trades.csv
"""
import re
import csv
import sys
from pathlib import Path

log_path = sys.argv[1] if len(sys.argv) > 1 else (
    r"C:\Users\User\AppData\Local\Temp\claude\D--03---------CSharp\tasks\be517fd.output"
)
output_dir = Path(r"D:\backtest_compare")

# Patterns
variant_re = re.compile(
    r"Variant: (A1_trailing_nowall_large2|A2_trailing_nowall_large3|"
    r"B1_trailing_askwall_large2|B2_trailing_askwall_large3|C_mode_c)"
)
date_re = re.compile(r"--date\s+(\d{4}-\d{2}-\d{2})")
stock_re = re.compile(r"Processing stock:\s*(\S+)")
open_re = re.compile(
    r"\[OPEN\]\s*Time:\s*(.+?),\s*Price:\s*([\d.]+),\s*DayHigh:\s*([\d.]+),\s*Ratio:\s*([\d.]+),\s*3sOutside:\s*([\d.]+)M"
)
close_re = re.compile(
    r"\[CLOSE\]\s*Time:\s*(.+?),\s*Price:\s*([\d.]+),\s*Reason:\s*(.+?),\s*PnL:\s*([+-]?[\d.]+)%"
)

# State
current_variant = None
current_date = None
current_stock = None
pending_open = None  # last [OPEN] not yet closed

# Collect trades per variant
from collections import defaultdict
variant_trades = defaultdict(list)

with open(log_path, "r", encoding="utf-8", errors="replace") as f:
    for line in f:
        # Track variant
        m = variant_re.search(line)
        if m:
            current_variant = m.group(1)
            pending_open = None
            continue

        # Track date from Command line
        m = date_re.search(line)
        if m:
            current_date = m.group(1)
            continue

        # Track stock
        m = stock_re.search(line)
        if m:
            current_stock = m.group(1)
            pending_open = None
            continue

        if not current_variant or not current_date or not current_stock:
            continue

        # Parse [OPEN]
        m = open_re.search(line)
        if m:
            pending_open = {
                "entry_time": m.group(1).strip(),
                "entry_price": float(m.group(2)),
                "day_high_at_entry": float(m.group(3)),
                "entry_ratio": float(m.group(4)),
                "entry_outside_3s_M": float(m.group(5)),
            }
            continue

        # Parse [CLOSE]
        m = close_re.search(line)
        if m and pending_open:
            trade = {
                "variant": current_variant,
                "date": current_date,
                "stock_id": current_stock,
                **pending_open,
                "exit_time": m.group(1).strip(),
                "exit_price": float(m.group(2)),
                "exit_reason": m.group(3).strip(),
                "pnl_pct": float(m.group(4)),
            }
            variant_trades[current_variant].append(trade)
            pending_open = None
            continue

# Write CSV files
output_dir.mkdir(parents=True, exist_ok=True)

fieldnames = [
    "variant", "date", "stock_id",
    "entry_time", "entry_price", "day_high_at_entry", "entry_ratio", "entry_outside_3s_M",
    "exit_time", "exit_price", "exit_reason", "pnl_pct",
]

total_all = 0
for variant, trades in sorted(variant_trades.items()):
    csv_path = output_dir / f"{variant}_trades.csv"
    with open(csv_path, "w", newline="", encoding="utf-8-sig") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(trades)
    total_all += len(trades)
    print(f"  {variant}: {len(trades)} trades -> {csv_path}")

# Also write a combined CSV
combined_path = output_dir / "all_variants_trades.csv"
with open(combined_path, "w", newline="", encoding="utf-8-sig") as f:
    writer = csv.DictWriter(f, fieldnames=fieldnames)
    writer.writeheader()
    for variant in sorted(variant_trades):
        writer.writerows(variant_trades[variant])
print(f"\n  Combined: {total_all} trades -> {combined_path}")
