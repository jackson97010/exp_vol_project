# 資料流與 I/O

## 輸入資料

### Per-stock Tick Data (主要模式)

```
路徑: D:\feature_data\feature\{date}\{stockId}.parquet
範例: D:\feature_data\feature\2026-01-14\2317.parquet
```

每檔股票一個 parquet 檔，只載入族群成員的股票。

主要欄位：
- `type` — "Trade" / "Depth" (只使用 Trade)
- `time` — 時間戳 (datetime)
- `price` — 成交價
- `volume` — 成交量
- `vwap` — VWAP (可選，未使用)
- `day_high` — 日高 (可選，未使用)

### Static Data

```
路徑: D:\feature_data\feature\{date}\static_data.parquet (或 .csv)
```

欄位：
- `stock_id` — 股票代碼
- `previous_close` — 昨收
- `monthly_avg_trading_value` — 月均成交金額
- `security_type` — "RR" 為處置股
- `prev_day_limit_up` — 前日是否漲停
- `prev_close_0050` — 0050 昨收 (大盤過濾用)

### Screening CSV

```
路徑: C:\Users\User\Documents\_02_bt\Backtest_tick_module\screening_results.csv
```

每日族群篩選結果，定義哪些股票屬於哪個族群。

欄位: `date, stock_id, stock_name, category, avg_amount_20d, today_amount, group_ma20_sum, group_today_sum, val_ratio`

支援中文 header (`日期, 代碼, 族群`)，自動處理 BOM。

### Close Prices (Replay 用)

```
路徑: D:\03_預估量相關資量\CSharp\close.parquet
```

Wide-format parquet: 日期為列，股票代碼為欄。用於 Replay 模式取得前一交易日收盤價。

---

## 輸出結構

### Per-date 輸出

```
{output_path}/{date}/trades_{date}.parquet
```

### Batch 彙總

```
{output_path}/batch_trades.parquet
```

### Parquet 輸出欄位 (ParquetExporter)

| 欄位 | 型別 | Nullable | 說明 |
|------|------|----------|------|
| stock_id | string | | 股票代碼 |
| entry_time | DateTime | | 進場時間 |
| entry_price | double | | 進場價 |
| entry_vwap | double | | 進場時 VWAP |
| entry_day_high | double | | 進場時日高 |
| total_shares | double | | 總股數 |
| position_cash | double | | 部位資金 |
| group_name | string | | 族群名稱 |
| group_rank | int | | 族群排名 |
| member_rank | int | | 成員排名 |
| exit_time | DateTime | Yes | 出場時間 |
| exit_price | double | Yes | 出場價 |
| exit_reason | string | | 出場原因 (stopLoss/timeExit/takeProfit/bailout/marketClose/groupRankDrop) |
| profit_taken | bool | | 是否有停利成交 |
| tp_fills | string | | TP 成交摘要 ("TP1@xxx;TP2@yyy" 或 "none") |
| pnl_amount | double | Yes | 損益金額 |
| pnl_percent | double | Yes | 損益百分比 |
| entry_ratio | double | | 進場價/VWAP 比率 % |

### Replay 輸出

```
{output_path}/replay/replay_data.json    — 秒級快照 JSON
{output_path}/replay/replay.html         — 瀏覽器可視化
```

---

## CLI 參數 (Console/Program.cs)

```
dotnet run -- --mode <single|batch|replay> [options]
```

| 參數 | 說明 |
|------|------|
| `--mode single/batch/replay` | 執行模式 |
| `--date YYYY-MM-DD` | 回測日期 |
| `--dates d1 d2 ...` | 多日期 (batch) |
| `--config path` | YAML 設定檔路徑 |
| `--output_path path` | 覆蓋輸出路徑 |
| `--group_csv path` | 覆蓋 group.csv 路徑 |
| `--tick_data path` | 覆蓋 tick data 基礎路徑 |
| `--static_data path` | 覆蓋 static data 路徑 |
| `--screening_csv path` | Screening CSV 路徑 |
| `--close_parquet path` | Close parquet 路徑 (replay) |

CLI 覆蓋通過 `config.RawConfig[key] = value` 直接修改設定字典。

環境變數: `STRONGEST_VWAP_OUTPUT_PATH` — 覆蓋輸出路徑 (低於 CLI)

---

## Parquet.Net 4.24.0 使用注意

### API 用法

```csharp
// 讀取
using var stream = File.OpenRead(path);
using var reader = await ParquetReader.CreateAsync(stream);
var fields = reader.Schema.GetDataFields();
for (int rg = 0; rg < reader.RowGroupCount; rg++) {
    using var rgReader = reader.OpenRowGroupReader(rg);
    var columns = new DataColumn[fields.Length];
    for (int c = 0; c < fields.Length; c++)
        columns[c] = await rgReader.ReadColumnAsync(fields[c]);
    // 按 row 遍歷...
}

// 寫入 (簡便方式)
var schema = new ParquetSchema(fields);
using var stream = File.Create(path);
await stream.WriteSingleRowGroupParquetFileAsync(schema, columns);
```

### 已知限制

- `DateTimeOffset` 不支援 → 必須使用 `DateTime`
- pyarrow 22+ 產生的 parquet 含 `SizeStatistics` 元資料，4.24.0 無法解析
  → 使用 `_compat` 版本 parquet (已移除 SizeStatistics)
- 5.x 移除了 `Parquet.Rows` 命名空間，目前維持 4.24.0

### 命名空間

```csharp
using Parquet;
using Parquet.Data;        // DataColumn
using Parquet.Schema;      // ParquetSchema, DataField
```

### DataField 定義

```csharp
new DataField("name", typeof(string))          // 必填
new DataField("name", typeof(double), true)    // nullable
```
