# C# 回測系統 (BacktestModule)

## 專案概述

C# 版本的 tick-by-tick 回測系統，從 Python 版本 (`Backtest_tick_module`) 翻譯而來。
策略配置以 **Bo_v2.yaml** 為 source of truth。

## 技術棧

- .NET 8.0 / C# 12
- Parquet.Net — 讀取 parquet 資料檔
- YamlDotNet — 載入 YAML 設定檔

## 目錄結構

```
BacktestModule/
├── Core/                  # 核心引擎
│   ├── BacktestEngine.cs  # 回測引擎（組裝器）
│   ├── BacktestLoop.cs    # Tick-by-tick 主迴圈
│   ├── Constants.cs       # 系統常數
│   ├── Interfaces.cs      # 介面定義
│   ├── Models.cs          # 舊 model 檔 (已遷移)
│   └── Models/            # 資料模型
│       ├── TickData.cs    # Tick 資料模型
│       ├── LoopState.cs   # 迴圈狀態
│       └── MetricsAccumulator.cs
├── Strategy/              # 策略模組
│   ├── ConfigLoader.cs    # YAML 設定載入器
│   ├── EntryLogic.cs      # 進場邏輯
│   ├── ExitLogic.cs       # 出場邏輯
│   ├── Indicators.cs      # 技術指標
│   ├── PositionManager.cs # 部位管理
│   ├── DataProcessor.cs   # 資料處理
│   ├── TickSizeHelper.cs  # 跳動單位計算
│   ├── SmallOrderFilter.cs # 小單過濾器
│   └── ParquetHelper.cs   # Parquet I/O
├── Analytics/             # 分析報告
├── Exporters/             # 匯出模組
├── Visualization/         # 圖表繪製
└── Console/               # 主程式入口
```

## 工作流程

1. 修改前先執行 `dotnet build` 確認現有程式碼能編譯
2. 修改後執行 `dotnet build` 確認無編譯錯誤
3. 設定值修改必須同步 `GetDefaultConfig()` 和 `LoadConfig()` 兩處

## 關鍵注意事項

- **Config 同步**: C# 預設值必須與 Python Bo_v2.yaml 保持一致
- **EntryLogic.cs / ExitLogic.cs** 中的 fallback 預設值也須同步
- **BacktestLoop.cs** 中的 fallback 預設值也須同步
- 所有 `Console.WriteLine` debug 輸出應可通過 verbose flag 控制
- Parquet 路徑: `D:\feature_data\feature\{date}\{stockId}.parquet`

## 編譯指令

```bash
cd D:\03_預估量相關資量\CSharp\BacktestModule
dotnet build
```

## 執行指令

```bash
# 單一標的回測
dotnet run -- --mode single --stock_id 2330 --date 2024-01-15 --config "C:\Users\User\Documents\_02_bt\Backtest_tick_module\Bo_v2.yaml"

# 批次回測（指定標的）
dotnet run -- --mode batch --date 2026-01-14 --stock_list 2317 3661 6488 --config "Bo_v2.yaml"

# 批次回測（使用篩選結果）
dotnet run -- --mode batch --date 2026-01-14 --use_screening --config "Bo_v2.yaml"

# 批次回測（使用自訂 CSV 篩選檔）
dotnet run -- --mode batch --date 2026-01-14 --use_screening --screening_file "screen_limit_up.csv" --config "Bo_v2.yaml"

# 啟用動態流動性門檻
dotnet run -- --mode batch --date 2026-01-14 --stock_list 2317 --use_dynamic_liquidity --config "Bo_v2.yaml"

# 覆蓋流動性乘數與上限
dotnet run -- --mode batch --date 2026-01-14 --stock_list 2317 --use_dynamic_liquidity --liquidity_multiplier 0.005 --liquidity_cap 30000000 --config "Bo_v2.yaml"

# 覆蓋進場時間
dotnet run -- --mode batch --date 2026-01-14 --stock_list 2317 --entry_start_time 09:05:00 --config "Bo_v2.yaml"

# 覆蓋輸出路徑
dotnet run -- --mode batch --date 2026-01-14 --stock_list 2317 --output_path "D:\C#_backtest\my_test" --config "Bo_v2.yaml"

# 不生成圖表
dotnet run -- --mode batch --date 2026-01-14 --stock_list 2317 --no_chart --config "Bo_v2.yaml"
```

## CLI 參數一覽

| 參數 | 說明 |
|---|---|
| `--mode <single\|batch>` | 執行模式 |
| `--stock_id <id>` | 標的代碼 (single mode) |
| `--date <YYYY-MM-DD>` | 回測日期 |
| `--stock_list <id1 id2 ...>` | 標的清單 (batch mode) |
| `--use_screening` | 使用篩選結果 CSV |
| `--screening_file <path>` | 自訂篩選 CSV 路徑 (支援中文 header: 日期/代碼) |
| `--config <path>` | YAML 設定檔路徑 |
| `--entry_start_time <HH:MM:SS>` | 覆蓋進場開始時間 |
| `--output_path <path>` | 覆蓋輸出路徑 |
| `--use_dynamic_liquidity` | 啟用動態流動性門檻 |
| `--liquidity_multiplier <value>` | 覆蓋門檻乘數 |
| `--liquidity_cap <value>` | 覆蓋門檻上限 |
| `--no_chart` | 不生成圖表 |
| `--no_csv` | 不輸出 CSV |
| `--no_ask_wall` | 停用大壓單出場 |
| `--stop_loss_ticks_large <N>` | 覆蓋大 tick 停損格數 |

## 動態流動性門檻 (Dynamic Liquidity Threshold)

- 資料檔: `daily_liquidity_threshold_compat.parquet` (wide-format: 日期為列, 股票代碼為欄)
- 位置: `D:\03_預估量相關資量\CSharp\daily_liquidity_threshold_compat.parquet`
- 格式: Pandas wide-format parquet (date 欄在最後一欄 index 2260)
- 使用 `_compat` 版本以相容 Parquet.Net 4.24.0 (避免 pyarrow 22+ SizeStatistics 問題)
- 公式: `resolved_threshold = min(raw_threshold * multiplier, cap)`
- Bo_v2.yaml 預設: `multiplier=1.0`, `cap=50,000,000`

## 圖表輸出

回測完成後自動生成互動式 Plotly HTML 圖表 (5 子圖):
1. 價格走勢 + Day High + 進出場標記 + 參考價 + 漲停價
2. Ratio 指標 + 進場門檻
3. Day High 成長率 + 動能衰竭區域
4. 委買/委賣掛單厚度
5. 買賣平衡比率

輸出路徑: `D:\回測結果\{date}\{stockId}_strategy_chart_{date}.html`

---

## 修改紀錄

### 2026-02-19: 動態流動性門檻 Parquet 讀取修復 + 圖表整合

**問題**: C# 版本無法正確讀取 Python 產生的 `daily_liquidity_threshold.parquet`

**修改檔案**:

1. **`Strategy/ParquetHelper.cs`** — 重寫流動性門檻 Parquet 讀取
   - `ReadLiquidityThresholdParquet`: 回傳型別改為 `Dictionary<DateTime, Dictionary<string, double>>` (wide-format)
   - `ReadLiquidityThresholdAsync`: 以欄名 ("date" / "__index_level_0__") 尋找日期欄，不再假設在 index 0
   - `ReadLiquidityThresholdAlternativeAsync`: 新增 table reader 備用方法
   - `GetDateTimeFromValue`: 新增奈秒 (ns) 偵測啟發式 (閾值 1e16)，同時支援微秒 (us) 與奈秒

2. **`Core/BacktestEngine.cs`** — 重寫動態門檻解析
   - `_liquidityThresholdCache` 型別改為 `Dictionary<DateTime, Dictionary<string, double>>`
   - `ResolveDynamicLiquidityThreshold`: 以 `.Date` 比對日期，支援 wide-format 快取
   - 搜尋路徑優先使用 `_compat` 版本 parquet
   - `GenerateOutputs`: 新增 `GenerateChart` 呼叫，自動產生 Plotly HTML 圖表

3. **`Strategy/ConfigLoader.cs`** — 同步預設值
   - `use_dynamic_liquidity_threshold`: `true` → `false` (Bo_v2.yaml 會覆蓋為 true)
   - `dynamic_liquidity_multiplier`: `1.0` → `0.004` (Bo_v2.yaml 會覆蓋為 1.0)

4. **`Strategy/EntryLogic.cs`** — 同步 fallback 預設值
   - `use_dynamic_liquidity_threshold` fallback: `true` → `false`

5. **`Console/Program.cs`** — 新增 CLI 參數
   - `--use_dynamic_liquidity`: 啟用動態大量搓合門檻
   - `--liquidity_cap <value>`: 覆蓋動態門檻上限
   - `--liquidity_multiplier <value>`: 覆蓋門檻乘數

**Parquet.Net 相容性**:
- 維持 Parquet.Net 4.24.0 (5.x 移除了 `Parquet.Rows` 命名空間，需完整重寫)
- pyarrow 22+ 產生的 parquet 含 `SizeStatistics` 元資料，Parquet.Net 4.24.0 無法解析
- 解法: 使用 `_compat` 版 parquet (已移除 SizeStatistics)

**驗證結果**:
- 單一標的 (1216, 2025-01-02): 正確讀取 4609 日期、2246 檔股票
- 批次回測 (2026-01-14, 294 檔): 182 檔有交易，動態門檻正確解析
- 圖表生成: 自動輸出互動式 HTML 圖表至 `D:\回測結果\{date}\`

### 2026-02-20: 大壓單出場 (Ask Wall Exit) + 觀察訊號 + 圖表改版

**新增功能**:

1. **觀察訊號 (Observation Signals)** — 僅標記、不觸發出場
   - 價漲量縮訊號 (`VolumeShrinkSignal`): 持倉期間價格創新高但 5s 外盤金額縮量
   - VWAP 乖離訊號 (`VwapDeviationSignal`): 持倉期間價格與 VWAP 正向乖離超過門檻

2. **大壓單出場 (Ask Wall Exit)** — 新出場類型
   - 3 AND 條件偵測: 委賣集中度 + bid_ask_ratio + VWAP 乖離
   - 觀察/確認機制: 偵測 → 觀察 15s → day_high 未創新高 → 確認出場 1/3
   - Day high 創新高自動取消觀察並重新偵測
   - 每次持倉最多觸發一次

3. **圖表改版** — 從 5 子圖改為 3 子圖 (對齊 Python)
   - Subplot 1 (50%): 價格 + Day High + 進出場標記
   - Subplot 2 (25%): Ratio 雙線 (ratio_15s_300s 藍實線 + ratio_15s_180s_w321 紫虛線)
   - Subplot 3 (25%): 漲跌幅 % (相對昨收)

**修改檔案**:

| 檔案 | 修改內容 |
|---|---|
| `Core/Models/TickData.cs` | +`Ratio15s180sW321`, +`VolumeShrinkSignal`, +`VwapDeviationSignal` |
| `Core/Models/MetricsAccumulator.cs` | +2 `List<bool>` 累積器 |
| `Core/Models/LoopState.cs` | +`AskWallState` class, +`MassiveThreshold` |
| `Core/Constants.cs` | +`OutsideVolumeWindow5s = 5` |
| `Core/BacktestEngine.cs` | +`OutsideVolumeTracker5s` |
| `Core/BacktestLoop.cs` | +5s tracker, +ask wall 狀態機, +訊號計算, +所有出場重置 |
| `Strategy/ExitLogic.cs` | +`CheckAskWallSignal()` (3 AND 條件) |
| `Strategy/ConfigLoader.cs` | +7 ask_wall config keys, +4 observation signal keys |
| `Strategy/PositionManager.cs` | +`EntryOutsideVolume5s` |
| `Strategy/ParquetHelper.cs` | +`ratio_15s_180s_w321` column |
| `Visualization/ChartCreator.cs` | 3 子圖改版 + 訊號標記 |
| `Console/Program.cs` | +`--output_path` CLI 參數 |

**Ask Wall 配置 (Bo_v2.yaml)**:
```yaml
ask_wall_exit_enabled: true
ask_wall_dominance_ratio: 3.0
ask_wall_min_amount_floor: 1000000
ask_wall_bid_ask_ratio: 2.0
ask_wall_vwap_deviation: 1.8
ask_wall_confirm_seconds: 15
ask_wall_exit_ratio: 0.333
```

**執行指令**:
```bash
# 大壓單出場回測（輸出至新資料夾）
dotnet run -- --mode batch --date 2026-01-14 --stock_list 2317 --use_dynamic_liquidity --config Bo_v2_csharp.yaml --output_path "D:\回測_外盤掛單"

# 全日期批次（PowerShell 腳本）
powershell -ExecutionPolicy Bypass -File run_ask_wall_backtest.ps1
```

**Code Review 修正**:
- `ExitLogic.cs`: `CheckAskWallSignal` 中 VWAP 改為 configurable `vwap_column` (原硬編碼 `row.Vwap`)
- `ChartCreator.cs`: xaxis2/xaxis3 加入 `matches:'x'` 同步縮放
- `ChartCreator.cs`: class XML comment 更新為 3 子圖
- `Console/Program.cs`: 新增 `BACKTEST_OUTPUT_PATH` 環境變數 fallback

**Unicode 路徑 workaround**:
- 非 UTF-8 shell 啟動 PowerShell 時，中文路徑會亂碼
- `run_ask_wall_backtest.ps1` 使用 ASCII 路徑 `D:\backtest_askwall`，完成後自動搬移至 `D:\回測_外盤掛單`

**驗證結果**:
- Build: 0 錯誤, 0 警告 (不含 pre-existing warnings)
- 單一標的 (2317, 2026-01-14): 正確輸出至 `D:\回測_外盤掛單\`
- 大壓單訊號: 偵測、觀察、取消、確認流程均正常
- 全日期回測 (2025-11-01 ~ 2026-02-10, 70 日): 輸出至 `D:\backtest_askwall\` → `D:\回測_外盤掛單\`

### 2026-02-20: Mode C 掛單停利 (exit_mode_c) 三階段出場

**新增功能**: Mode C 三階段分批出場策略，與 trailing_stop 互斥

**三階段機制**:
- **Stage 0 → Stage 1**: 大壓單出場 1/3 (ask wall) OR fallback 跌破 low_1m 1/3 (hard stop loss 有效)
- **Stage 1 → Stage 2**: price < vwap_5m × (1 - deviation_pct%) 出場 1/3 (hard stop loss 有效)
- **Stage 2 → Stage 3**: price ≤ low_3m 出場剩餘 (僅 entry price protection，無 hard stop loss)

**自動行為**: Mode C 啟用時自動停用 trailing_stop 並強制啟用 entry_price_protection

**修改檔案**:

| 檔案 | 修改內容 |
|---|---|
| `Core/Models/TickData.cs` | +`Vwap5m` 欄位 |
| `Core/Models/LoopState.cs` | +`ModeCState` class (CurrentStage, AskWallTriggered, PathDecided) |
| `Strategy/ParquetHelper.cs` | +`vwap_5m` column 讀取 |
| `Strategy/ConfigLoader.cs` | +`ParseModeCConfig()`, +exit_mode_c 預設值與載入 |
| `Strategy/ExitLogic.cs` | +`CheckVwap5mDeviationExit()`, +`CheckLow1mExit()`, +`CheckLow3mExit()`, Mode C 自動停用 trailing_stop |
| `Strategy/PositionManager.cs` | +`mode_c_stage1/2/3` exit level tracking |
| `Core/BacktestLoop.cs` | +`ProcessModeCExit()` 三階段狀態機, +ModeC.Reset() 於所有出場/收盤重置 |

**Mode C 配置 (Bo_v2.yaml)**:
```yaml
exit_mode_c:
  enabled: true
  vwap_5m_deviation_pct: 0.3
  vwap_5m_column: vwap_5m
  stage1_exit_ratio: 0.333
  stage2_exit_ratio: 0.333
  stage3_exit_ratio: 1.0
```

**驗證結果**:
- Build: 0 錯誤, 0 警告

### 2026-02-21: 效能優化 (Performance Optimization)

**目標**: 批次回測 24 小時 → 預估 3-8 倍加速

**Phase 1: 低風險高效益優化**:

| 項目 | 檔案 | 說明 |
|---|---|---|
| ① MetricsAccumulator 預配置 | `Core/Models/MetricsAccumulator.cs`, `Core/BacktestLoop.cs` | 11 個 List 用 `data.Count` 初始化容量，避免動態擴容 |
| ② OrderBook inline sums | `Strategy/Indicators.cs` | 消除 `CalculateBid/AskThickness`/`CalculateBalanceRatio` 中每 tick 3 次 `new double[5]` 堆積配置 |
| ③ Config 值快取 | `Core/BacktestLoop.cs` | 17 個熱路徑 config 值快取為 `readonly` 欄位，避免每 tick Dictionary 查詢 + 型別檢查 |
| ④ 動態門檻 O(1) 查詢 | `Core/BacktestEngine.cs` | `_dateOnlyLookup` Dictionary 取代 `foreach` 線性搜尋 |
| ⑤ indicators Dict 重複使用 | `Core/BacktestLoop.cs` | `_indicatorsBuffer` 欄位 `.Clear()` + 重填，不再每次 `new Dictionary` |
| ⑥ 消除 tick 全掃描 | `Core/BacktestLoop.cs` | 刪除 `data.Where(t => t.Time == currentTime ...)` O(n) 掃描，改用已追蹤的 `lastBidAskRatio` |
| ⑦ SmallOrderFilter 預快取 | `Strategy/SmallOrderFilter.cs` | `PreFilterTradeData()` 一次性過濾 + `BinarySearchLastBefore()` O(log n) 取代 O(n) |

**Phase 2: 中等工程量優化**:

| 項目 | 檔案 | 說明 |
|---|---|---|
| ⑧ Sync-over-Async 消除 | `Strategy/ParquetHelper.cs` | `Task.Run(async () => ...).Result` → `.GetAwaiter().GetResult()`，減少執行緒池開銷 |
| ⑨ ExitResult 強型別 | `Strategy/ExitLogic.cs`, `Core/BacktestLoop.cs` | `Dictionary<string, object>` → 具名屬性 class，消除 boxing/unboxing，保留 indexer 向後相容 |
| ⑩ 批次平行化 | `Core/BacktestEngine.cs` | `foreach` → `Parallel.ForEach`，每檔建立獨立 per-stock engine clone，共享唯讀快取 |

**平行化架構**:
- `BacktestEngine(BacktestEngine parent)` 私有建構子：clone entry config (mutable)，共享 exit/reentry config (read-only)
- 共享 `_liquidityThresholdCache` + `_dateOnlyLookup` (唯讀)
- 共享 `DataProcessor` (需先呼叫 `GetCompanyName("__preload__")` 強制初始化 lazy cache)
- `MaxDegreeOfParallelism = Environment.ProcessorCount`
- 結果收集用 `ConcurrentBag<T>`

**驗證結果**:
- Build: 0 錯誤, 0 新增警告

### 2026-02-23: 最低漲幅門檻 (min_price_change_pct) + 自訂篩選 CSV + CLI 增強

**新增功能**:

1. **最低漲幅門檻 (min_price_change_pct)** — 進場價格相對昨收漲幅必須 > X% 才允許進場
   - 與現有 `price_change_limit_pct`（漲幅上限）搭配，形成漲幅區間過濾
   - 例：`min_price_change_pct=7.0` + `price_change_limit_pct=9.0` → 只進場 7%-9% 漲幅的點位

2. **自訂篩選 CSV (`--screening_file`)** — 批次回測可指定任意 CSV 檔案
   - 不再需要覆蓋 `screening_results.csv`
   - 支援中文 header (`日期`/`代碼`) 和英文 header (`date`/`stock_id`/`code`)
   - 自動處理 BOM (UTF-8 with BOM)

3. **CLI 參數覆蓋進場時間** — `--entry_start_time` + `--output_path` 組合
   - 單一 YAML 即可跑多組進場時間測試，不需要多份 config

**修改檔案**:

| 檔案 | 修改內容 |
|---|---|
| `Strategy/ConfigLoader.cs` | +`min_price_change_enabled` / `min_price_change_pct` (GetDefaultConfig + LoadConfig + GetEntryConfig) |
| `Strategy/EntryLogic.cs` | +Step 2b: min price change filter (在 price_change_limit 之後檢查) |
| `Console/Program.cs` | +`--screening_file` CLI 參數, +中文 header 支援 (`日期`/`代碼`), +BOM 處理 |

**min_price_change 配置 (Bo_v2_modeC.yaml)**:
```yaml
min_price_change_enabled: true
min_price_change_pct: 7.0    # 只進場漲幅 > 7% 的點位
price_change_limit_pct: 9.0  # 漲幅 > 9% 不進場
```

**執行範例**:
```bash
# 使用自訂漲停股 CSV + 覆蓋進場時間 + 覆蓋輸出路徑
dotnet run -- --mode batch --date 2026-01-30 --use_screening --screening_file screen_limit_up.csv --use_dynamic_liquidity --config Bo_v2_modeC_limitup.yaml --entry_start_time 09:05:00 --output_path "D:\C#_backtest\t-1_limit_up_0905"
```

**批次回測腳本**:

| 腳本 | 說明 |
|---|---|
| `run_modeC_over7.ps1` | ModeC + min 7% 無上限，screening_results.csv 271 日 |
| `run_modeC_7to9.ps1` | ModeC + 7%-9% 區間，screening_results.csv 271 日 |
| `run_modeC_limitup.ps1` | ModeC + 原始 8.5% 上限，screen_limit_up.csv (前一天漲停股) 251 日 |
| `run_limitup_entry_times.ps1` | ModeC 漲停股 × 4 進場時間 (0905/0906/0907/0908) |
| `run_modeC_entry_times.ps1` | ModeC 7-9% × 4 進場時間 (0905/0906/0907/0908) |

**回測結果彙總**:

| 輸出資料夾 | 設定 | 日期數 | 交易明細 |
|---|---|---|---|
| `ModeC_Over7` | min 7%, max 8.5%, entry 0909 | 265 | 1,529 |
| `ModeC_7to9` | min 7%, max 9%, entry 0909 | 265 | 1,646 |
| `ModeC_7to9_0908` | min 7%, max 9%, entry 0908 | 265 | 1,656 |
| `ModeC_7to9_0907` | min 7%, max 9%, entry 0907 | 265 | 1,667 |
| `ModeC_7to9_0906` | min 7%, max 9%, entry 0906 | 265 | 1,671 |
| `ModeC_7to9_0905` | min 7%, max 9%, entry 0905 | 265 | 1,675 |
| `t-1_limit_up` | 漲停股, max 8.5%, entry 0909 | 223 | 827 |
| `t-1_limit_up_0908` | 漲停股, max 8.5%, entry 0908 | 223 | 837 |
| `t-1_limit_up_0907` | 漲停股, max 8.5%, entry 0907 | 223 | 849 |
| `t-1_limit_up_0906` | 漲停股, max 8.5%, entry 0906 | 224 | 858 |
| `t-1_limit_up_0905` | 漲停股, max 8.5%, entry 0905 | 226 | 868 |

**篩選 CSV 格式支援**:
- 英文 header: `date,stock_id` 或 `date,code`
- 中文 header: `日期,代碼` (如 `screen_limit_up.csv`)
- 自動偵測 BOM，欄位位置自動辨識

**驗證結果**:
- Build: 0 錯誤
- 所有回測組別均正確輸出至對應資料夾

### 2026-03-02: 大量搓合窗口可配置 (massive_matching_window) + 12 組網格回測

**新增功能**: `massive_matching_window` 從硬編碼常數改為 YAML 可配置參數

**修改檔案**:

| 檔案 | 修改內容 |
|---|---|
| `Strategy/ConfigLoader.cs` | +`massive_matching_window` (GetDefaultConfig=1, LoadConfig 從 YAML 讀取, GetEntryConfig keys) |
| `Core/BacktestEngine.cs` | 主建構子 + clone 建構子: 從 `EntryConfig["massive_matching_window"]` 讀取，fallback `Constants.MassiveMatchingWindow` |

**新增腳本**:

| 腳本 | 說明 |
|---|---|
| `run_limit_up_grid.ps1` | 12 組網格回測: `strategy_b_stop_loss_ticks_small` (3/4/5/6) × `massive_matching_window` (1s/2s/3s) |

**YAML 配置**:
```yaml
massive_matching_window: 1  # seconds, default 1s (可設 1/2/3)
```

**回測矩陣** (12 組):

| stop_loss_ticks | mm_window | output |
|---|---|---|
| 3 | 1s/2s/3s | `limit_up_sl3_mm{1,2,3}s` |
| 4 | 1s/2s/3s | `limit_up_sl4_mm{1,2,3}s` |
| 5 | 1s/2s/3s | `limit_up_sl5_mm{1,2,3}s` |
| 6 | 1s/2s/3s | `limit_up_sl6_mm{1,2,3}s` |

**執行指令**:
```bash
cd D:\03_預估量相關資量\CSharp\BacktestModule
powershell -ExecutionPolicy Bypass -File run_limit_up_grid.ps1
```

**驗證結果**:
- Build: 0 錯誤, 0 警告

### 2026-03-04: Mode D 百分比停損 + 分鐘低點分批停利 (exit_mode_d)

**新增功能**: Mode D 三階段分批出場策略，使用百分比停損取代 tick 停損

**三階段機制**:
- **Stage 0 (100%)**: 百分比停損 (全出) OR 破 stage1_field → 出場 33.3%
- **Stage 1 (66.7%)**: 百分比停損 (全出) OR 破 stage2_field → 出場 33.3%
- **Stage 2 (33.4%)**: Entry price protection (全出) OR 破 stage3_field → 出場剩餘
- 停損: `price <= entryPrice × (1 - stop_loss_pct/100)`，預設 1.2%
- 自動停用 trailing_stop、強制啟用 entry_price_protection

**修改檔案**:

| 檔案 | 修改內容 |
|---|---|
| `Core/Models/LoopState.cs` | +`ModeDState` class (CurrentStage, Reset()), +`ModeD` property |
| `Core/Models/TickData.cs` | +`Low7m` 欄位, +`GetFieldByName("low_7m")` |
| `Strategy/ParquetHelper.cs` | +`low_7m` column 讀取 |
| `Strategy/ConfigLoader.cs` | +`ParseModeDConfig()`, +exit_mode_d 預設值與載入, +GetExitConfig keys |
| `Strategy/ExitLogic.cs` | +Mode D 屬性, +`CheckModeDStopLoss()`, +`CheckModeDLowExit()` |
| `Strategy/PositionManager.cs` | +`mode_d_stage1/2/3` exit level tracking |
| `Core/BacktestLoop.cs` | +`ProcessModeDExit()` 三階段狀態機, +ModeD.Reset() 於所有出場/收盤重置 |

**兩個版本**:
- **版本 A (1-3-5)**: `Bo_v2_modeD_135.yaml` — stage1=low_1m, stage2=low_3m, stage3=low_5m
- **版本 B (3-5-7)**: `Bo_v2_modeD_357.yaml` — stage1=low_3m, stage2=low_5m, stage3=low_7m

**執行指令**:
```bash
cd D:\03_預估量相關資量\CSharp\BacktestModule
powershell -ExecutionPolicy Bypass -File run_modeD_screening.ps1
```

**驗證結果**:
- Build: 0 錯誤, 0 新增警告
