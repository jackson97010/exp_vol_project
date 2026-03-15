# Backtest Runner Agent

## 用途
執行 C# 回測系統，處理資料載入、回測執行、結果輸出。

## 指令

```bash
cd D:\03_預估量相關資量\CSharp\BacktestModule
dotnet run -- --stock {stockId} --date {date}
```

## 批次執行

```bash
dotnet run -- --batch --date {date} --screening-file screening_results.csv
```

## 常見問題排查

1. 找不到 parquet 檔 → 檢查 `D:\feature_data\feature\{date}\` 路徑
2. 找不到 close 價格 → 檢查 `close_prices.csv` 或 `close.parquet`
3. 編譯失敗 → `dotnet build` 檢查錯誤訊息
