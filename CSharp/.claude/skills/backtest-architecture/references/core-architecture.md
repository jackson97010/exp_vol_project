# Core 層架構

## BacktestEngine.cs (`Core/BacktestEngine.cs`)

組裝器，負責資料載入與結果輸出。

### 關鍵方法

- `RunSingleDate(date)` — 單日回測流程：
  1. 從 screening CSV 或 group CSV 載入族群定義 (`GroupDefinition`)
  2. 載入 static data (parquet/csv): 昨收、月均量、證券類型
  3. 載入 tick data (per-stock parquet 或 merged parquet)
  4. 建立 `BacktestLoop` 並執行
  5. 輸出結果 (Parquet + 統計)

- `RunBatch(dates)` — 多日批次回測：
  - 逐日呼叫 `RunSingleDate`
  - 匯總所有交易至 `batch_trades.parquet`

### 資料載入模式

```
Per-stock mode (主要使用):
  D:\feature_data\feature\{date}\{stockId}.parquet
  每檔股票獨立 parquet，只載入族群成員的股票

Merged mode:
  D:\feature_data\feature\{date}\all_ticks.parquet
  所有股票合併在一個檔案
```

### 輸出結構

```
{output_path}/
├── {date}/
│   └── trades_{date}.parquet    # 單日交易明細
└── batch_trades.parquet          # 所有日期匯總
```

---

## BacktestLoop.cs (`Core/BacktestLoop.cs`)

Tick-by-tick 多股票主迴圈，所有股票的 tick 依時間順序處理。

### 建構子參數

```csharp
BacktestLoop(StrategyConfig config, Dictionary<string, GroupDefinition> groups,
             Dictionary<string, StockStaticData> staticData)
```

### ProcessTick 流程

每個 tick 執行以下步驟：

```
1. 0050 tick → 更新 MarketFilter (大盤漲幅檢查)
2. 非交易 tick (tradeCode != 1, ETF) → 跳過
3. 大盤未啟用 → 跳過
4. 更新 IndexData (價格、量、VWAP、DayHigh)
5. 更新漲停鎖定狀態
6. [持倉] → ExitManager.CheckExit (停損 > 時間 > 停利 > bailout)
7. [持倉 + groupRankExitEnabled] → 族群排名下降出場
8. StrongGroupScreener.OnTick → 取得 MatchInfo (族群+成員排名)
9. [無 match] → 重置 Signal A 狀態
10. [Signal A 啟用] → 兩階段偵測 (near VWAP → bounce)
11. [Mode E] → DayHigh 突破進場條件
12. OrderTrigger.TryEntry → 建立 TradeRecord + 停利單
13. PositionManager.OpenPosition
```

### 關鍵狀態追蹤

- `_allStocks: Dictionary<string, IndexData>` — 所有股票即時狀態
- `_enteredStocks: HashSet<string>` — 當日已進場股票 (每檔一次)
- `_signalAEnabled` — 是否啟用 Signal A (false = Mode E)
- `_groupRankExitEnabled` — 族群排名下降出場

### Mode E 進場條件

```csharp
bool isDayHighBreakout = idx.DayHigh > idx.PrevDayHigh && idx.PrevDayHigh > 0;
signalTriggered = tod >= _entryStartTime
    && tod < _entryEndTime
    && isDayHighBreakout
    && !_enteredStocks.Contains(stockId)
    && !_positionManager.HasPosition(stockId);
```

### 族群排名出場

```csharp
if (currentRank == 0 ||
    (currentRank >= _groupRankExitThreshold && currentRank > trade.EntryGroupRank))
    exitReason = "groupRankDrop";
```

---

## 資料模型

### IndexData (`Core/Models/IndexData.cs`)

每檔股票的即時累積狀態：

| 屬性 | 型別 | 說明 |
|------|------|------|
| StockId | string | 股票代碼 |
| PreviousClose | double | 昨收 |
| LastPrice | double | 最新成交價 |
| Vwap | double | 成交量加權平均價 (CumulativePriceVolume / CumulativeVolume) |
| DayHigh | double | 當日最高價 |
| DayLow | double | 當日最低價 |
| PrevDayHigh | double | 上一 tick 的 DayHigh (用於偵測突破) |
| CumulativeVolume | double | 累積成交量 |
| TodayCumulativeValue | double | 累積成交金額 |
| MonthlyAvgTradingValue | double | 月均成交金額 |
| LimitUpPrice | double | 漲停價 |
| IsLimitUpLocked | bool | 是否漲停鎖住 |
| PrevDayLimitUp | bool | 前一日是否漲停 |
| PriceChangePct | computed | (LastPrice - PreviousClose) / PreviousClose |
| VwapChangePct | computed | (Vwap - PreviousClose) / PreviousClose |

### RawTick (`Core/Models/RawTick.cs`)

單筆原始 tick：Time, StockId, Price, Volume, TradeCode, TickType, AskPrice0, SecurityType, PreviousClose, MonthlyAvgTradingValue, TodayCumulativeValue, IsLimitUpLocked, PrevDayLimitUp

### TradeRecord (`Core/Models/TradeRecord.cs`)

完成的交易記錄：

- 進場: StockId, EntryTime, EntryPrice, EntryVwap, EntryDayHigh, TotalShares, PositionCash
- 族群: EntryGroupName, EntryGroupRank, EntryMemberRank
- 停利: TakeProfitOrders (List<TakeProfitOrder>), ProfitTaken, RemainingShares
- 出場: ExitTime, ExitPrice, ExitReason, IsFullyClosed
- 損益: PnlAmount, PnlPercent (由 CalculatePnl() 計算)

### TakeProfitOrder

```csharp
public class TakeProfitOrder {
    public int Index;           // 0-4
    public double TargetPrice;
    public double Shares;
    public bool Filled;
    public DateTime? FillTime;
    public string Type;         // "takeProfit" or "limitUp"
}
```

### LoopState (`Core/Models/LoopState.cs`)

- `GlobalState` — MarketEnabled, DisableReason
- `SignalAState` — NearVwap, LowSinceNear, Triggered, Forbidden
- `MatchInfo` — GroupName, GroupRank, MemberRank, RawMemberRank, M1Symbol

### Constants (`Core/Constants.cs`)

```csharp
MarketOpen = 09:00:00
MarketClose = 13:30:00
MarketRallyCheckTime = 09:15:00
DefaultOutputDir = @"D:\回測結果"
TickDataBasePath = @"D:\feature_data\feature"
Symbol0050 = "0050"
```
