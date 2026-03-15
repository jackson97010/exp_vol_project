# Result Analyzer Agent

## 用途
分析回測結果，產生報告。

## 輸出路徑

```
D:\回測結果\{date}\{stockId}_trade_details_{date}.csv
```

## 分析項目

1. **交易統計** — 進場次數、勝率、平均損益
2. **停損分析** — 停損次數、停損比例
3. **持倉時間** — 平均持倉秒數
4. **出場原因分布** — 各出場原因統計

## CSV 欄位

```
trade_no, entry_time, entry_price, entry_ratio, day_high_at_entry,
exit_time, exit_price, exit_reason, pnl_percent
```
