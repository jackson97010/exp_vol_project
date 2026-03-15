# StrongestVwap C# 回測系統架構知識庫

## 概覽

StrongestVwap 是一個 C# (.NET 8.0) 多股票族群篩選回測系統。核心邏輯：每個 tick 更新所有股票狀態 → 族群排名 → 篩選成員 → 訊號觸發 → 進出場管理。

專案路徑：`D:\03_預估量相關資量\CSharp\StrongestVwap\`

## 技術棧

- .NET 8.0 / C# 12
- Parquet.Net 4.24.0 (讀寫 parquet)
- YamlDotNet 16.3.0 (載入 YAML 設定)

## 目錄結構

```
StrongestVwap/
├── Core/                       # 核心引擎
│   ├── BacktestEngine.cs       # 組裝器：載入資料 → 跑迴圈 → 輸出結果
│   ├── BacktestLoop.cs         # Tick-by-tick 多股票主迴圈
│   ├── Constants.cs            # 系統常數 (市場時間、路徑)
│   └── Models/
│       ├── IndexData.cs        # 每檔股票的即時狀態 (VWAP, DayHigh, 量等)
│       ├── RawTick.cs          # 原始 tick 資料
│       ├── TradeRecord.cs      # 完成交易記錄 + TP 訂單
│       └── LoopState.cs        # 狀態模型 (GlobalState, SignalAState, MatchInfo)
├── Strategy/                   # 策略模組
│   ├── ConfigLoader.cs         # YAML 設定載入 (StrategyConfig)
│   ├── StrongGroup.cs          # 族群篩選核心 (排名 + 成員選擇)
│   ├── SignalA.cs              # Signal A 兩階段進場訊號
│   ├── OrderTrigger.cs         # 進場執行 + 停利單配置
│   ├── ExitLogic.cs            # 出場邏輯 (停損/時間/停利/bailout)
│   ├── MarketFilter.cs         # 大盤過濾 (0050 漲幅檢查)
│   ├── PositionManager.cs      # 多股票持倉管理
│   ├── DataLoader.cs           # 資料載入 (parquet/CSV)
│   ├── OfflineScreener.cs      # 離線族群篩選 (使用收盤價)
│   └── TickSizeHelper.cs       # 台股跳動單位計算
├── Analytics/
│   └── TradeStatistics.cs      # 交易統計彙總
├── Exporters/
│   ├── ParquetExporter.cs      # Parquet 格式輸出交易結果
│   └── CsvExporter.cs          # CSV 格式輸出交易結果
├── Replay/
│   ├── ReplayEngine.cs         # 秒級族群回放引擎
│   └── ReplayExporter.cs       # 回放結果 JSON 輸出
└── Console/
    └── Program.cs              # CLI 入口 (single/batch/replay 模式)
```

## 架構層次

1. **Core 層** — BacktestEngine (組裝) + BacktestLoop (主迴圈) + 資料模型
2. **Strategy 層** — 族群篩選、進出場邏輯、設定管理
3. **Analytics 層** — 交易統計
4. **Exporters 層** — Parquet/CSV 輸出
5. **Replay 層** — 秒級族群回放可視化

## 參考文件

- [Core 架構](references/core-architecture.md) — 引擎、迴圈、資料模型詳解
- [Strategy 模組架構](references/strategy-modules-architecture.md) — 族群篩選、進出場、訊號邏輯
- [資料流](references/data-flow.md) — 輸入輸出、Parquet 格式、CLI 參數
- [設定參數參考](references/config-reference.md) — 所有 YAML 設定參數一覽
- [回測模式與腳本](references/modes-and-scripts.md) — Mode E、網格回測、批次腳本
