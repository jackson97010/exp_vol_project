# Strategy 層架構

## ConfigLoader.cs — StrategyConfig

YAML 設定載入器。使用 `Dictionary<string, object>` 儲存所有設定值。

### 架構

```csharp
public class StrategyConfig {
    private readonly Dictionary<string, object> _config;

    // 建構：GetDefaultConfig() → 讀取 YAML → MergeSection(strategy/signal_a/strong_group/order)
    public StrategyConfig(string configPath)

    // 型別安全存取
    public double GetDouble(string key, double defaultVal)
    public int GetInt(string key, int defaultVal)
    public bool GetBool(string key, bool defaultVal)
    public string GetString(string key, string defaultVal)
    public TimeSpan GetTimeSpan(string key, TimeSpan defaultVal)
    public List<double> GetDoubleList(string key)

    // 直接存取底層字典 (CLI 覆蓋用)
    public Dictionary<string, object> RawConfig => _config;
}
```

### YAML 結構

```yaml
strategy:          # 大盤過濾
signal_a:          # Signal A 進場訊號
strong_group:      # 族群篩選
order:             # 進出場 + 停利參數
```

### 修改注意

新增設定參數時必須同步：
1. `GetDefaultConfig()` — 預設值
2. YAML 中的對應 section
3. 使用該參數的類別建構子 (通常用 `config.GetXxx()` 讀取)

---

## StrongGroup.cs — 族群篩選核心

### 類別結構

```
GroupDefinition      — 族群定義 (GroupName, MemberStockIds)
GroupState           — 族群即時狀態 (排名、成員、有效性)
MemberState          — 成員即時狀態 (排名、漲幅、條件通過)
StrongGroupScreener  — 主篩選器
```

### 篩選流程 (OnTick)

```
1. UpdateGroupStates(allStocks)
   ├── 遍歷所有族群定義
   ├── 過濾月均量 < member_min_month_trading_val 的成員
   ├── 計算族群平均漲幅 (簡單平均 or 加權平均)
   ├── 計算族群交易量比率 (todayCumVal / monthlyAvg)
   ├── 有效性檢查: 月均量 >= 30億 AND 平均漲幅 > 門檻 AND 量比 >= 門檻
   ├── RankMembers: 按 VwapChangePct 排名, 排除漲停鎖住/漲幅>8.5%
   └── 族群按平均漲幅排名 (降序)

2. 遍歷有效族群, 尋找 stockId 是否在前 N 名成員中
   ├── 一般模式: top_group_max_select / normal_group_max_select
   └── Mode E: GetSelectedMembersModeE (動態選取)
```

### Mode E 動態成員選擇

```csharp
GetSelectedMembersModeE(GroupState gs):
  1. 計算 validMemberCount (FilteredRank > 0)
  2. targetSelect = count >= 大族群門檻 ? 大族群選取數 : 小族群選取數
  3. candidates = 排除處置股, 按 FilteredRank 排序
  4. 遍歷 candidates:
     - 漲停鎖住 → 跳過 (cascade), limitUpSkipCount++
     - 超過 max_limit_up_skip → 停止選取
     - 未漲停 → 加入 selected (直到達 targetSelect)
```

### 成員排名邏輯

```
Raw rank:      排除漲停鎖住 + 漲幅>8.5% → 按 VwapChangePct 降序排名
Filtered rank: 在 raw 基礎上再排除 cond1/cond2/cond4 + 處置股 + 前日漲停股
```

### GetCurrentGroupRank

```csharp
// 用於族群排名出場判斷
public int GetCurrentGroupRank(string groupName)
  → 有效族群返回 Rank, 無效返回 0
```

---

## SignalA.cs — 兩階段進場訊號

原始模式 (signal_a_enabled=true) 的進場訊號。Mode E 不使用。

### 兩階段偵測

```
Phase 1 (Near VWAP):
  price / vwap <= vwap_near_ratio (1.008)
  → 記錄 NearVwap=true, LowSinceNear=price

Phase 2 (Bounce):
  (price - LowSinceNear) / LowSinceNear >= bounce_ratio (0.008)
  → Triggered=true, 觸發進場
```

### 防護機制

- 已觸發 → 永不再觸發 (每檔一次)
- Pre-condition: price <= vwap * 0.997 → Forbidden (永久封鎖)
- 漲幅 > 8.5% → 不觸發
- 未被族群選中 → Reset NearVwap

---

## OrderTrigger.cs — 進場執行

### TryEntry 過濾順序

```
1. 進場時間限制 (entry_time_limit)
2. 前日漲停過濾 (filter_prev_day_limit_up)
3. 處置股過濾 (disposition_stocks_enabled)
4. 已持倉過濾
5. 最大進場價過濾 (max_entry_price = 1000)
6. 股數計算: floor(position_cash / entryPrice)
```

### 停利單模式

**Mode E (mode_e_exit=true)**: 基於進場價

```
TP1: entryPrice × (1 + pct[0]) × ratio[0] 的股數    例: +1.2%, 20%
TP2: entryPrice × (1 + pct[1]) × ratio[1] 的股數    例: +1.6%, 20%
TP3: entryPrice × (1 + pct[2]) × ratio[2] 的股數    例: +2.0%, 20%
TP4: 漲停價 × 剩餘 40% 股數
所有 TP 價格上限 = 漲停價
```

**原始模式**: 基於 DayHigh

```
TP1~3: dayHigh × (1 + pct[i]), 均分股數
LimitUp: 漲停價 × 2 份 (最後一份吸收餘數)
```

---

## ExitLogic.cs — ExitManager

### CheckExit 優先順序

```
1. 停損 (最高優先):
   - stop_loss_pct > 0: price <= entryPrice × (1 - pct/100)  例: -1.2%
   - 否則: price <= entryVwap × stop_loss_ratio_a (0.995)

2. 時間出場: time >= exit_time_limit (13:00 or 13:20)

3. 停利成交檢查: price >= tp.TargetPrice → 標記 Filled, 扣除 RemainingShares
   - 全部成交 → return "takeProfit"

4. Bailout (僅 ProfitTaken 後): price <= entryDayHigh × bailout_ratio
   - Mode E 停用 (bailout_enabled=false)
```

---

## MarketFilter.cs — 大盤過濾

追蹤 0050 ETF 價格，兩個時間點檢查：

```
09:00 (MarketOpen): 開盤漲幅 < market_open_min_chg → 停用
09:15 (RallyCheck): 漲幅 >= market_rally_disable_threshold (0.2) → 停用
```

`ShouldSkipTick`: tradeCode != 1 或 stockId 以 "00" 開頭 → 跳過

---

## PositionManager.cs — 持倉管理

```csharp
// 活躍持倉 (stockId → TradeRecord)
Dictionary<string, TradeRecord> _positions

// 已完成交易
List<TradeRecord> _completedTrades

OpenPosition(trade)     — 加入持倉
HasPosition(stockId)    — 是否持倉
GetPosition(stockId)    — 取得持倉
ClosePosition(stockId, time, price, reason) — 平倉 + 計算 PnL + 移至 completed
ForceCloseAll(time, allStocks) — 收盤全部平倉 (reason="marketClose")
```

---

## DataLoader.cs — 資料載入

### 族群定義載入

```
LoadGroupCsv(path)                    — 從 group.csv 載入 (group_name, stock_id)
LoadGroupsFromScreeningCsv(path, date) — 從 screening_results.csv 載入指定日期
  支援 header: date/stock_id/category 或 日期/代碼/族群
  自動處理 BOM
```

### Tick 資料載入

```
LoadPerStockTickData(baseDir, date, stockIds, staticData)
  → 遍歷 {baseDir}/{date}/{stockId}.parquet
  → 只載入 type=="Trade" 的 tick
  → 依時間排序

LoadTickDataParquet(path)
  → 載入 merged all_ticks.parquet
```

### Static Data 載入

```
LoadStaticData(path) → .csv 或 .parquet
  欄位: stock_id, previous_close, monthly_avg_trading_value, security_type, prev_day_limit_up, prev_close_0050
```

### Parquet 讀取技巧

- 使用 `ParquetReader.CreateAsync` + `OpenRowGroupReader` + `ReadColumnAsync`
- 時間戳解析: >1e16 = ns, >1e13 = us, otherwise = ms (epoch-based)
- 欄位名稱容錯: `FindColumn(fieldNames, "name1", "name2", ...)` 多候選名

---

## TickSizeHelper.cs — 台股跳動單位

```
>= 1000: 5.0
>= 500:  1.0
>= 100:  0.5
>= 50:   0.1
>= 10:   0.05
< 10:    0.01

CeilToTick(price)  — 無條件進位至跳動單位
FloorToTick(price) — 無條件捨去至跳動單位
CalculateLimitUp(prevClose) — 漲停價 = floor(prevClose × 1.10, tick)
```

---

## OfflineScreener.cs — 離線族群篩選

使用收盤價 (close.parquet) + screening_results.csv 進行事後族群篩選分析。
不在回測迴圈中使用，僅用於離線分析。

---

## Replay 模組

### ReplayEngine.cs

秒級族群回放引擎：
1. 載入 screening CSV → 建立族群成員映射
2. 載入 close.parquet → 取得前一日收盤價
3. 載入所有成員 tick data → 按時間合併排序
4. 每秒產生 TimeSnapshot (族群排名 + 成員排名 + 選中狀態)

### ReplayExporter.cs

將 ReplayResult 序列化為 JSON (System.Text.Json)，搭配 Web/replay.html 做瀏覽器可視化。
