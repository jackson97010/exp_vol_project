import os, json, subprocess, time, sys

os.environ['PYTHONIOENCODING'] = 'utf-8'

with open(r'D:\03_預估量相關資量\CSharp\BacktestModule\batch_all_stocks.json', 'r') as f:
    date_stocks = json.load(f)

dates = sorted(date_stocks.keys())
total_dates = len(dates)
total_stocks = sum(len(v) for v in date_stocks.values())

configs = [
    ('0905', 'Bo_v2_modeC_0905.yaml', r'D:\C#_backtest\ModeC_result\entry_0905'),
    ('0906', 'Bo_v2_modeC_0906.yaml', r'D:\C#_backtest\ModeC_result\entry_0906'),
    ('0907', 'Bo_v2_modeC_0907.yaml', r'D:\C#_backtest\ModeC_result\entry_0907'),
    ('0908', 'Bo_v2_modeC_0908.yaml', r'D:\C#_backtest\ModeC_result\entry_0908'),
]

cwd = r'D:\03_預估量相關資量\CSharp\BacktestModule'

overall_start = time.time()
print('=== ModeC Batch Runner ===')
print('Dates: %d, Stocks/config: %d, Configs: %d' % (total_dates, total_stocks, len(configs)))
print('Total runs: %d' % (total_stocks * len(configs)))
print()

for ci, (label, config, output_path) in enumerate(configs):
    config_start = time.time()
    print('>>> Config %d/4: entry_%s (%s)' % (ci+1, label, config))
    
    completed = 0
    errors = []
    trade_dates = 0
    
    for i, date in enumerate(dates):
        stocks = date_stocks[date]
        
        if (i+1) % 10 == 0 or i == 0:
            elapsed = time.time() - config_start
            rate = completed / elapsed if elapsed > 0 else 0
            remaining = (total_stocks - completed) / rate if rate > 0 else 0
            print('  [%d/%d] %s: %d stocks (done: %d/%d, %.0fs elapsed, ~%.0fm left)' % (
                i+1, total_dates, date, len(stocks), completed, total_stocks, elapsed, remaining/60))
            sys.stdout.flush()
        
        cmd = ['dotnet', 'run', '--', '--mode', 'batch', '--date', date,
               '--stock_list'] + stocks + [
               '--use_dynamic_liquidity', '--config', config,
               '--output_path', output_path, '--no_chart']
        
        try:
            result = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True,
                                   timeout=600, encoding='utf-8', errors='replace')
            completed += len(stocks)
            
            if 'trade(s) recorded' in result.stdout:
                trade_dates += 1
            
            if result.returncode != 0:
                errors.append((date, (result.stderr or 'unknown')[:100]))
                
        except subprocess.TimeoutExpired:
            errors.append((date, 'TIMEOUT'))
            completed += len(stocks)
        except Exception as e:
            errors.append((date, str(e)[:100]))
            completed += len(stocks)
    
    config_elapsed = time.time() - config_start
    print('  DONE: entry_%s in %.0fs (%.1fmin), %d dates with trades, %d errors' % (
        label, config_elapsed, config_elapsed/60, trade_dates, len(errors)))
    if errors:
        for d, e in errors[:5]:
            print('    ERROR %s: %s' % (d, e))
    print()
    sys.stdout.flush()

overall_elapsed = time.time() - overall_start
print('=== ALL DONE ===')
print('Total time: %.0fs (%.1fmin / %.1fh)' % (overall_elapsed, overall_elapsed/60, overall_elapsed/3600))
