"""
ModeC Batch Runner - Using screening_results.csv
Runs backtest for each date with its screening stocks using Bo_v2_modeC.yaml.
Output: D:\C#_backtest\ModeC_Momentum (no PNG charts)
"""
import csv, subprocess, time, sys
from collections import defaultdict

SCREENING_CSV = r'C:\Users\User\Documents\_02_bt\Backtest_tick_module\screening_results.csv'
CONFIG = 'Bo_v2_modeC.yaml'
OUTPUT_PATH = r'D:\C#_backtest\ModeC_Momentum'
CWD = r'D:\03_預估量相關資量\CSharp\BacktestModule'

# Parse screening_results.csv into {date: [stock_ids]}
date_stocks = defaultdict(list)
with open(SCREENING_CSV, 'r', encoding='utf-8') as f:
    reader = csv.DictReader(f)
    for row in reader:
        date_stocks[row['date']].append(row['stock_id'])

dates = sorted(date_stocks.keys())
total_dates = len(dates)
total_stocks = sum(len(v) for v in date_stocks.values())

print('=== ModeC Screening Batch Runner ===')
print(f'Config: {CONFIG}')
print(f'Output: {OUTPUT_PATH}')
print(f'Dates: {total_dates}, Total stocks: {total_stocks}')
print(f'Date range: {dates[0]} ~ {dates[-1]}')
print()
sys.stdout.flush()

overall_start = time.time()
completed_stocks = 0
trade_dates = 0
errors = []

for i, date in enumerate(dates):
    stocks = date_stocks[date]
    elapsed = time.time() - overall_start
    rate = completed_stocks / elapsed if elapsed > 0 else 0
    remaining = (total_stocks - completed_stocks) / rate if rate > 0 else 0

    print(f'[{i+1}/{total_dates}] {date}: {len(stocks)} stocks '
          f'(done: {completed_stocks}/{total_stocks}, '
          f'{elapsed:.0f}s elapsed, ~{remaining/60:.0f}m left)')
    sys.stdout.flush()

    cmd = ['dotnet', 'run', '--no-build', '-c', 'Release', '--',
           '--mode', 'batch', '--date', date,
           '--stock_list'] + stocks + [
           '--use_dynamic_liquidity',
           '--config', CONFIG,
           '--output_path', OUTPUT_PATH,
           '--no_chart']

    try:
        result = subprocess.run(
            cmd, cwd=CWD, capture_output=True, text=True,
            timeout=600, encoding='utf-8', errors='replace')
        completed_stocks += len(stocks)

        if 'trade(s) recorded' in result.stdout or 'stock(s) had trades' in result.stdout:
            trade_dates += 1

        if result.returncode != 0:
            errors.append((date, (result.stderr or 'unknown')[:200]))

    except subprocess.TimeoutExpired:
        errors.append((date, 'TIMEOUT'))
        completed_stocks += len(stocks)
    except Exception as e:
        errors.append((date, str(e)[:200]))
        completed_stocks += len(stocks)

overall_elapsed = time.time() - overall_start
print()
print('=== DONE ===')
print(f'Total time: {overall_elapsed:.0f}s ({overall_elapsed/60:.1f}min / {overall_elapsed/3600:.1f}h)')
print(f'Dates with trades: {trade_dates}/{total_dates}')
print(f'Errors: {len(errors)}')
if errors:
    print('First 10 errors:')
    for d, e in errors[:10]:
        print(f'  {d}: {e}')
