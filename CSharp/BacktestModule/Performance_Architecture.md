# 回測系統效能架構分析

這套系統能在 ~30 分鐘處理整年 tick-by-tick 資料，核心依靠 **三層加速架構**。

---

## 第一層：批次平行化 (最大貢獻，3-8x)

**`BacktestEngine.cs` — Clone + Parallel.ForEach 模式**

```
主 Engine (全域快取)
  ├─ Clone → Stock A engine (獨立 mutable 狀態)
  ├─ Clone → Stock B engine (獨立 mutable 狀態)
  ├─ Clone → Stock C engine (獨立 mutable 狀態)
  └─ ...最多 min(CPU cores, 8) 個同時跑
```

- 每檔股票 clone 一個獨立引擎，**零共享可變狀態**，完全無鎖
- 共享唯讀資源：流動性門檻快取、MA5 Bias 快取、DataProcessor
- `ConcurrentBag<T>` 收集結果
- 預先呼叫 `GetCompanyName("__preload__")` 強制初始化 lazy cache，避免平行時的 race condition

---

## 第二層：資料載入優化

| 技術 | 說明 |
|---|---|
| **Parquet Column Projection** | 檔案可能有 50+ 欄，只讀取需要的 25 欄，大幅減少 I/O |
| **Column Array 預解析** | 迴圈前一次性把所有欄位解析成 Array 引用，迴圈中直接 `arr.GetValue(i)` |
| **流動性門檻 O(1) 查詢** | `_dateOnlyLookup` Dictionary 取代 `foreach` 線性搜尋 (~4,600 日期 × 2,200 股票) |
| **CSV 優先載入** | MA5 Bias 快取優先讀 CSV (~100x 比 parquet 快)，找不到才 fallback parquet |
| **Sync-over-Async** | `.GetAwaiter().GetResult()` 取代 `Task.Run()`，省去 ThreadPool 排程開銷 |

---

## 第三層：每 Tick 熱路徑優化

每檔每天 ~5,000-10,000 個 tick，這些優化在內迴圈中逐 tick 累積：

### 1. Config 值快取 (17 個)

```csharp
// 迴圈前快取為 readonly 欄位
private readonly TimeSpan _cfgEntryStartTime;
private readonly bool _cfgEntryBufferEnabled;
// 省去每 tick 的 Dictionary.TryGetValue + object 拆箱 + 型別轉換
```

### 2. Dictionary Buffer 重用

```csharp
private readonly Dictionary<string, double> _indicatorsBuffer = new(12);
// 每 tick Clear() + 重填，不 new Dictionary
```

### 3. 指標計算 Inline Sum（避免堆積配置）

```csharp
// 舊: new double[5] { bid1, bid2, ... }.Sum()  ← 每 tick 3 次陣列配置
// 新: 直接 sum += bid1; sum += bid2; ...        ← 零配置
```

### 4. LinkedList 滑動窗口

```csharp
// O(1) 加尾 + O(1) 移頭，vs List 的 O(n) RemoveAt(0)
_dayHighHistory.AddLast(...);
_dayHighHistory.RemoveFirst();
```

### 5. SmallOrderFilter 二分搜尋

```csharp
// 舊: data.Where(t => t.Time < target).Last() → O(n)
// 新: BinarySearchLastBefore() → O(log n)
// 5,000 ticks: 12 次比較 vs 5,000 次 → 400x
```

### 6. MetricsAccumulator 預配置容量

```csharp
var metrics = new MetricsAccumulator(data.Count);  // 11 個 List 預設大小
// 避免 List<T> 內部陣列倍增重配置
```

### 7. ExitResult 強型別

```csharp
// 舊: Dictionary<string, object> → boxing/unboxing + string hash + 型別檢查
// 新: ExitResult class → 直接屬性存取，零拆箱
```

---

## 整體效能估算

```
每日單檔: ~5,000-10,000 ticks × 1 pass = ~0.1 秒/檔
每日批次: ~100-300 檔 ÷ 8 cores ≈ 1-5 秒/日
整年: ~250 交易日 × ~5 秒 = ~20-30 分鐘
```

---

## 效能影響總表

| 優化項目 | 範圍 | 影響 |
|---|---|---|
| Parallel.ForEach 平行化 | 批次模式 | 3-8x (cores × efficiency) |
| Config 值快取 | 每 tick | 17 次 Dictionary 查詢 + 型別轉換省略 |
| Parquet Column Projection | 每檔 | I/O 頻寬減少 ~50% |
| 流動性門檻 O(1) 查詢 | 每檔 | O(n) → O(1)，避免 ~10M 次迭代 |
| 二分搜尋 (SmallOrderFilter) | 每次進場判斷 | O(n) → O(log n)，~400x |
| Inline Sum (Indicators) | 每 tick × 3 | 零陣列配置 |
| LinkedList 滑動窗口 | 每 tick | O(n) → O(1) 窗口過期 |
| MetricsAccumulator 預配置 | 每檔 | 10-20 次 List 擴容省略 |
| ExitResult 強型別 | 每次出場 | 零 boxing/unboxing |
| Dictionary Buffer 重用 | 每 tick | 1 次 Dictionary 配置省略 |
| Sync-over-Async | 每檔 | ThreadPool 排程開銷省略 |

---

## 關鍵架構決策

1. **Clone Pattern**: 每檔獨立引擎，消除鎖競爭與快取失效
2. **Lazy Initialization**: 全域快取載入一次，平行共享
3. **Single-Pass Loop**: 所有 tick 只走一遍，進場/出場/指標/觀察訊號全在同一個 `for` 迴圈完成，無回頭掃描
4. **Strong Typing**: ExitResult 消除配置/轉換開銷
5. **LinkedList**: 滑動窗口操作常數時間
6. **Column Projection**: 減少 parquet 反序列化開銷
