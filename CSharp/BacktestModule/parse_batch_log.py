"""Parse batch backtest log to extract per-variant trade statistics."""
import re
import sys
from collections import defaultdict

log_path = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\User\AppData\Local\Temp\claude\D--03---------CSharp\tasks\be517fd.output"

current_variant = None
variant_trades = defaultdict(list)

# Variant header appears once per variant section
variant_pattern = re.compile(r"Variant: (A1_trailing_nowall_large2|A2_trailing_nowall_large3|B1_trailing_askwall_large2|B2_trailing_askwall_large3|C_mode_c)")
# [CLOSE] lines contain PnL
close_pattern = re.compile(r"\[CLOSE\].*PnL: ([+-]?\d+\.?\d*)%")

with open(log_path, "r", encoding="utf-8", errors="replace") as f:
    for line in f:
        m = variant_pattern.search(line)
        if m:
            current_variant = m.group(1)

        if current_variant:
            m2 = close_pattern.search(line)
            if m2:
                pnl = float(m2.group(1))
                variant_trades[current_variant].append(pnl)

# Print summary
print()
print(f"{'Variant':<35} {'Trades':>7} {'Win':>5} {'Loss':>5} {'Flat':>5} {'WinRate':>8} {'TotalPnL':>10} {'AvgPnL':>8} {'MaxWin':>8} {'MaxLoss':>9}")
print("=" * 115)

for v in ["A1_trailing_nowall_large2", "A2_trailing_nowall_large3",
          "B1_trailing_askwall_large2", "B2_trailing_askwall_large3",
          "C_mode_c"]:
    trades = variant_trades.get(v, [])
    if not trades:
        print(f"{v:<35} {'N/A':>7}")
        continue

    n = len(trades)
    wins = sum(1 for p in trades if p > 0)
    losses = sum(1 for p in trades if p < 0)
    flat = sum(1 for p in trades if p == 0)
    win_rate = wins / n * 100 if n else 0
    total_pnl = sum(trades)
    avg_pnl = total_pnl / n if n else 0
    max_win = max(trades) if trades else 0
    max_loss = min(trades) if trades else 0

    print(f"{v:<35} {n:>7} {wins:>5} {losses:>5} {flat:>5} {win_rate:>7.1f}% {total_pnl:>+9.2f}% {avg_pnl:>+7.3f}% {max_win:>+7.2f}% {max_loss:>+8.2f}%")

# Profit factor
print()
print(f"{'Variant':<35} {'GrossWin':>10} {'GrossLoss':>10} {'PF':>6}")
print("-" * 65)
for v in ["A1_trailing_nowall_large2", "A2_trailing_nowall_large3",
          "B1_trailing_askwall_large2", "B2_trailing_askwall_large3",
          "C_mode_c"]:
    trades = variant_trades.get(v, [])
    if not trades:
        continue
    gross_win = sum(p for p in trades if p > 0)
    gross_loss = abs(sum(p for p in trades if p < 0))
    pf = gross_win / gross_loss if gross_loss > 0 else float('inf')
    print(f"{v:<35} {gross_win:>+9.2f}% {-gross_loss:>+9.2f}% {pf:>5.2f}")
