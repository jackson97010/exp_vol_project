# StrongestVwap 策略完整文件

## 目錄

1. [系統概覽](#1-系統概覽)
2. [選股流程](#2-選股流程)
3. [盤中進場條件](#3-盤中進場條件)
4. [盤中出場條件](#4-盤中出場條件)
5. [部位管理與停利停損](#5-部位管理與停利停損)
6. [策略版本對照表](#6-策略版本對照表)
7. [跳動單位表](#7-跳動單位表)
8. [參數總覽](#8-參數總覽)

---

## 1. 系統概覽

### 架構

```
screening_results.csv (族群定義)
         │
         ▼
   BacktestEngine (組裝器：載入資料、啟動回測)
         │
         ▼
   BacktestLoop (Tick-by-tick 主迴圈)
         │
    ┌─────┼─────┬──────────┬──────────┐
    ▼     ▼     ▼          ▼          ▼
MarketFilter  StrongGroup  ExitManager  OrderTrigger  PositionManager
(大盤過濾)   (族群篩選)    (出場邏輯)   (進場執行)    (部位追蹤)
```

### 每筆 Tick 處理流程

```
收到 Tick
  │
  ├─ Depth tick → 更新五檔委買委賣量 → return
  ├─ 0050/ETF tick → 更新大盤過濾器 → return
  ├─ trade_code ≠ 1 (非一般交易) → skip
  ├─ 大盤過濾器未啟用 → skip
  │
  ▼
更新 IndexData (LastPrice, DayHigh, DayLow, VWAP, 累計量值)
  │
  ▼
若持有部位 → 檢查出場條件 → 若觸發出場 → return
  │
  ▼
族群篩選 → 此股票是否被選中？ → 否 → return
  │
  ▼
進場訊號評估 (Signal A / Mode E DayHigh / 大量搓合)
  │
  ▼
通過所有前置過濾 → 建立部位 + 掛停利單
```

---

## 2. 選股流程

### 2.1 資料來源

族群定義來自 `screening_results.csv`，格式：

| 欄位           | 說明                        |
| -------------- | --------------------------- |
| date           | 日期 (YYYY-MM-DD)           |
| stock_id       | 股票代碼                    |
| category       | 族群名稱 (例：半導體、光電) |
| avg_amount_20d | 20 日均量                   |

系統根據當日 `date` 欄位篩選該日的族群成員。

### 2.2 大盤過濾 (MarketFilter)

以 0050 作為大盤代理指標：

| 檢查時機   | 條件                                                 | 結果         |
| ---------- | ---------------------------------------------------- | ------------ |
| 09:00 開盤 | `(0050開盤價 - 昨收) / 昨收 < market_open_min_chg` | 全日停止交易 |
| 09:15      | `(0050當前價 - 昨收) / 昨收 ≥ 20%`                | 全日停止交易 |

> 目前 `market_open_min_chg = 0.0`，開盤檢查實質無效。Rally 檢查為安全閥，防止極端行情。

### 2.3 族群驗證 (每筆 Tick 即時更新)

每個族群必須同時滿足以下條件才算有效：

| # | 條件                      | 參數                            | 預設值        |
| - | ------------------------- | ------------------------------- | ------------- |
| 1 | 族群月均成交值 ≥ 門檻    | `group_min_month_trading_val` | 30 億         |
| 2 | 族群平均漲幅 > 門檻       | `group_min_avg_pct_chg`       | -999 (無限制) |
| 3 | 今日累計值 / 月均 ≥ 門檻 | `group_min_val_ratio`         | 0.0 (無限制)  |
| 4 | 至少 1 名有效成員         | —                              | —            |

> 在目前的 Mode E 配置中，條件 2 和 3 均設為無限制 (`-999` 和 `0.0`)，這個是因為我給你的清單就會有所有符合量價的標的，還有清單在這個清單之下的標的都可以去做篩選還有判斷。

### 2.4 成員驗證

每個族群成員需通過以下過濾：

| # | 條件                   | 說明                                                                   |
| - | ---------------------- | ---------------------------------------------------------------------- |
| 1 | `月均成交值 ≥ 3 億` | `member_min_month_trading_val = 300,000,000`                         |
| 2 | **非**漲停鎖定   | `IsLimitUpLocked = false`                                            |
| 3 | 漲幅 < 8.5%            | `PriceChangePct < 0.085`                                             |
| 4 | 排除處置股             | `SecurityType ≠ "RR"` (當 `exclude_disposition_from_rank = true`) |

> 可選條件 (目前均關閉)：
>
> - Cond1: 量比門檻 (`member_cond1_enabled = false`)
> - Cond2: 價格+VWAP 組合 (`member_cond2_enabled = false`)
> - Cond4: VWAP 漲幅門檻 (`member_cond4_enabled = false`)
>
> 同理其他的也是。

### 2.5 成員排名

**排名依據**: VWAP 漲幅 % (降序)

```
RawRank:      未經 Cond1/2/4 過濾的排名 (僅排除漲停鎖定 + 漲幅>8.5%)
FilteredRank: 經過所有過濾後的最終排名
```

### 2.6 族群排名

所有有效族群按**平均漲幅 %** (降序) 排名，只取前 `group_valid_top_n = 5` 名族群。

### 2.7 成員選取 (Mode E)

Mode E 動態選取規則：

| 族群大小        | 選取人數 |
| --------------- | -------- |
| ≥ 5 名有效成員 | 3 名     |
| < 5 名有效成員  | 2 名     |

**漲停遞補機制 (Limit-Up Cascade)**：

```
依 FilteredRank 順序遍歷成員：
  ├─ 該成員漲停鎖定 → 跳過，遞補下一位 (最多跳 5 次)
  ├─ 該成員為處置股 → 跳過
  └─ 該成員正常 → 選入
```

> 若連續遇到超過 5 位漲停鎖定的成員，停止選取。

### 2.8 選股輸出結果 (MatchInfo)

```
GroupName:    "半導體"          (族群名稱)
GroupRank:    1                 (族群排名)
MemberRank:   2                (在族群中的排名)
M1Symbol:    "2330"            (族群第一名)
GroupMembers: "M1:2330|M2:3661|M3:6488"  (所有被選中的成員)
```

---

## 3. 盤中進場條件

### 3.1 版本 A：延伸時段 DayHigh 突破

**進場時間**：09:05 ~ 13:00

**觸發條件**（全部需滿足）：

| # | 條件                      | 說明                                                |
| - | ------------------------- | --------------------------------------------------- |
| 1 | `price > DayHigh`       | 當前價格創盤中新高                                  |
| 2 | `DayHigh > PrevDayHigh` | DayHigh 確實被突破 (PrevDayHigh 為突破前的 DayHigh) |
| 3 | 時間在 09:05 ~ 13:00 之間 | 進場時段                                            |
| 4 | 該股票今日尚未進場過      | 每股每日限進場一次                                  |
| 5 | 該股票目前無持倉          | 不疊加部位                                          |
| 6 | **Ask > Bid**       | 五檔委賣量 > 五檔委買量                             |

> Ask > Bid 的資料來自最新的 Depth tick，代表突破時委賣掛單量大於委買掛單量。

### 3.2 版本 B：族群選股 + 大量搓合品質過濾

**進場時間**：09:09 ~ 13:00

在版本 A 的所有條件之上，**額外**需通過以下品質濾網（全部需滿足）：

| # | 過濾器                 | 條件                            | 參數                             |
| - | ---------------------- | ------------------------------- | -------------------------------- |
| 1 | **漲幅上限**     | `(價格 - 昨收) / 昨收 < 8.5%` | `price_change_limit_pct = 8.5` |
| 2 | **大量搓合金額** | 1 秒窗口內外盤金額 ≥ 動態門檻  | `massive_matching_window = 1`  |
| 3 | **急拉濾網**     | 5 分鐘漲幅 < 3%                 | `interval_pct_threshold = 3.0` |
| 4 | **量比濾網**     | ratio_15s_300s ≥ 3.0           | `ratio_entry_threshold = 3.0`  |

#### 大量搓合金額計算

```
每筆 tick 到達時：
1. 清除超過 1 秒的歷史記錄
2. 若為外盤成交 (tick_type = 1)：
   trade_amount = price × volume × 1000  (張 → 股 → 金額)
   加入滑動窗口，累加 OutsideVolumeAmount
3. 檢查 OutsideVolumeAmount ≥ threshold
```

#### 動態流動性門檻

門檻值依股票和日期動態查詢，來自 `daily_liquidity_threshold_compat.parquet`：

```
門檻 = parquet 中該股票該日的原始值
若查不到 → fallback 使用固定值 50,000,000 (5 千萬)
```

### 3.3 前置過濾 (OrderTrigger)

通過進場訊號後，仍需通過以下過濾才能建立部位：

| # | 過濾器       | 條件                                                     |
| - | ------------ | -------------------------------------------------------- |
| 1 | 進場時間     | `time < entry_time_limit` (13:00)                      |
| 2 | 前日漲停     | `filter_prev_day_limit_up` 啟用時跳過前日漲停股        |
| 3 | 處置股       | `disposition_stocks_enabled = false` 時跳過處置股 (RR) |
| 4 | 已持有       | 不重複建倉                                               |
| 5 | 最高價限制   | `price ≤ 1000`                                        |
| 6 | 部位計算有效 | `Floor(10,000,000 / 價格) > 0`                         |

---

## 4. 盤中出場條件

### 出場優先順序

每筆 tick 對持有部位按以下順序檢查，觸發後立即出場，**同一筆 tick 不再檢查進場**：

```
1. 停損 (最高優先) → 全部出場
2. 時間出場         → 全部出場
3. 停利單觸發       → 分批出場
4. Trailing Low     → 分批出場 (50% + 50%)
5. Bailout          → 全部出場 (Mode E 停用)
6. 族群排名下降      → 全部出場 (可選，目前停用)
7. 收盤強制平倉      → 全部出場
```

### 4.1 停損

**Mode E 百分比停損**：

```
stop_price = entry_price × (1 - 1.2%)
           = entry_price × 0.988

若 price ≤ stop_price → 全部出場，reason = "stopLoss"
```

> 注意：停損為 tick-level 觸發，非保證價格。快速下跌時可能跳空穿越停損價，實際損失可能 > 1.2%。

### 4.2 時間出場

```
若 time ≥ 13:00 → 全部出場，reason = "timeExit"
```

### 4.3 停利單觸發

預掛停利單，逐一檢查：

```
若 price ≥ TP_target_price → 標記已成交，減少剩餘股數
若所有股數售完 → reason = "takeProfit"
```

停利單配置詳見 [第 5 節](#52-停利掛單)。

### 4.4 Trailing Low (分鐘低點出場)

**啟用條件**: `trailing_low_enabled = true`

```
Stage 0 → Stage 1：price ≤ low_10m
  → 出場 50% 剩餘股數

Stage 1 → Stage 2：price ≤ low_15m
  → 出場剩餘全部股數
  → reason = "trailingLow"

若 Stage 0 時同時 price ≤ low_10m 且 ≤ low_15m → 一次全出
```

> `trailing_low_require_tp_fill = false` 時，不需要停利觸發就可啟用 trailing low。

### 4.5 Bailout

```
條件：bailout_enabled = true 且已觸發停利 且 price ≤ entryDayHigh × 0.8
→ 全部出場，reason = "bailout"
```

> Mode E 停用此功能 (`bailout_enabled = false`)。

### 4.6 族群排名下降出場

```
條件：group_rank_exit_enabled = true
若族群當前排名 = 0 (已失效) 或 (排名 ≥ 門檻 且 排名 > 進場時排名)
→ 全部出場，reason = "groupRankDrop"
```

> 目前停用 (`group_rank_exit_enabled = false`)。

### 4.7 收盤強制平倉

```
所有 tick 處理完畢後，若仍有未平倉部位：
→ 以最後一筆 tick 的價格全部平倉
→ reason = "marketClose"
```

---

## 5. 部位管理與停利停損

### 5.1 部位大小

```
每檔部位資金 = 10,000,000 (1 千萬)
總股數 = Floor(10,000,000 / 進場價格)
每股每日限進場一次
```

### 5.2 停利掛單 (Mode E: Entry-Price Based)

進場時自動掛出 4 檔停利單：

| 停利檔位 | 目標價                       | 出場比例 | 股數                 |
| -------- | ---------------------------- | -------- | -------------------- |
| TP1      | entry_price × 1.008 (+0.8%) | 25%      | Floor(total × 0.25) |
| TP2      | entry_price × 1.016 (+1.6%) | 25%      | Floor(total × 0.25) |
| TP3      | entry_price × 1.024 (+2.4%) | 25%      | Floor(total × 0.25) |
| 漲停     | 漲停價                       | 25%      | 剩餘全部             |

> 目標價若超過漲停價，則以漲停價為準。
> 目標價會無條件進位到最近的跳動單位 (CeilToTick)。

### 5.3 停損

```
停損價 = entry_price × (1 - 1.2/100) = entry_price × 0.988
觸發條件：price ≤ 停損價
出場：剩餘全部股數
```

### 5.4 PnL 計算

```
已成交停利 PnL = Σ (TP成交價 - entry_price) × TP股數
Trailing Low PnL = Σ (TL成交價 - entry_price) × TL股數
剩餘股 PnL = (exit_price - entry_price) × remaining_shares

總 PnL = 已成交停利 PnL + Trailing Low PnL + 剩餘股 PnL
PnL % = 總 PnL / (entry_price × total_shares) × 100
```

---

## 6. 策略版本對照表

### 目前 4 組比較配置

| 配置         | 進場時段    | Ask>Bid | 大量搓合           | 急拉濾網            | 量比濾網              | 漲幅上限              | 動態門檻      | 排除前日漲停  |
| ------------ | ----------- | ------- | ------------------ | ------------------- | --------------------- | --------------------- | ------------- | ------------- |
| **A1** | 09:05~13:00 | Yes     | No                 | No                  | No                    | No                    | No            | **Yes** |
| **A2** | 09:05~13:00 | Yes     | No                 | No                  | No                    | No                    | No            | No            |
| **B1** | 09:09~13:00 | Yes     | **Yes** (1s) | **Yes** (<3%) | **Yes** (≥3.0) | **Yes** (<8.5%) | **Yes** | **Yes** |
| **B2** | 09:09~13:00 | Yes     | **Yes** (1s) | **Yes** (<3%) | **Yes** (≥3.0) | **Yes** (<8.5%) | **Yes** | No            |

### 回測結果 (2025-01-03 ~ 2026-01-30, 263 日)

| 配置         | 交易次數        | PF             | 勝率 | 平均損益%       | 總損益  | Max Loss |
| ------------ | --------------- | -------------- | ---- | --------------- | ------- | -------- |
| A1           | 4,060           | 1.57           | —   | —              | +110.5M | -2.46%   |
| A2           | 4,299           | 1.55           | —   | —              | +115.8M | -4.34%   |
| **B1** | **2,592** | **1.97** | —   | **0.41%** | +106.5M | -1.86%   |
| **B2** | **2,729** | **1.96** | —   | **0.41%** | +113.0M | -1.86%   |

> B1/B2 品質顯著優於 A1/A2：較高 PF、較高平均損益%、較少停損出場。

### 共同設定

以下設定在 4 組配置中相同：

| 參數                          | 值                                        |
| ----------------------------- | ----------------------------------------- |
| signal_a_enabled              | false (使用 Mode E)                       |
| mode_e_enabled                | true                                      |
| group_valid_top_n             | 5 (前 5 族群)                             |
| mode_e_large_group_select     | 3                                         |
| mode_e_small_group_select     | 2                                         |
| mode_e_limit_up_cascade       | true (漲停遞補)                           |
| mode_e_max_limit_up_skip      | 5                                         |
| member_min_month_trading_val  | 3 億                                      |
| group_min_month_trading_val   | 30 億                                     |
| exclude_disposition_from_rank | true (排除處置股)                         |
| position_cash                 | 1,000 萬                                  |
| stop_loss_pct                 | 1.2%                                      |
| bailout_enabled               | false                                     |
| exit_time_limit               | 13:00                                     |
| trailing_low_enabled          | true                                      |
| trailing_low_require_tp_fill  | false                                     |
| TP 檔位                       | +0.8%, +1.6%, +2.4% (各 25%) + 漲停 (25%) |

---

## 7. 跳動單位表

台灣證券交易所跳動單位：

| 價格區間       | 跳動單位 (Tick Size) |
| -------------- | -------------------- |
| < 10 元        | 0.01                 |
| 10 ~ 49.95 元  | 0.05                 |
| 50 ~ 99.9 元   | 0.10                 |
| 100 ~ 499.5 元 | 0.50                 |
| 500 ~ 999 元   | 1.00                 |
| ≥ 1000 元     | 5.00                 |

**漲停價計算**：

```
漲停價 = Floor(昨收 × 1.10 / tick_size) × tick_size
```

**停利價進位**：

```
TP 目標價 = Ceil(entry_price × (1 + pct) / tick_size) × tick_size
```

> 停損觸發但實際成交可能因跳動單位產生滑價。例如 43 元區間 tick = 0.05，停損價 42.53 可能直接成交在 42.25。

---

## 8. 參數總覽

### 大盤過濾

| 參數                               | 值  | 說明                      |
| ---------------------------------- | --- | ------------------------- |
| `market_rally_disable_threshold` | 0.2 | 0050 漲幅 ≥ 20% 停止交易 |
| `market_open_min_chg`            | 0.0 | 開盤檢查 (目前無效)       |

### 族群篩選

| 參數                                | 值            | 說明                        |
| ----------------------------------- | ------------- | --------------------------- |
| `group_min_month_trading_val`     | 3,000,000,000 | 族群月均成交值門檻          |
| `member_min_month_trading_val`    | 300,000,000   | 成員月均成交值門檻          |
| `group_min_avg_pct_chg`           | -999          | 族群平均漲幅門檻 (無限制)   |
| `group_min_val_ratio`             | 0.0           | 今日/月均比門檻 (無限制)    |
| `group_valid_top_n`               | 5             | 有效族群數上限              |
| `exclude_disposition_from_rank`   | true          | 排除處置股                  |
| `exclude_prev_limit_up_from_rank` | false         | 不排除前日漲停 (在排名階段) |
| `is_weighted_avg`                 | false         | 簡單平均 (非加權)           |

### Mode E 選取

| 參數                             | 值   | 說明                |
| -------------------------------- | ---- | ------------------- |
| `mode_e_enabled`               | true | 啟用 Mode E         |
| `mode_e_large_group_threshold` | 5    | 大族群定義 (≥5 人) |
| `mode_e_large_group_select`    | 3    | 大族群選取人數      |
| `mode_e_small_group_select`    | 2    | 小族群選取人數      |
| `mode_e_limit_up_cascade`      | true | 漲停遞補            |
| `mode_e_max_limit_up_skip`     | 5    | 最大遞補次數        |

### 進場

| 參數                           | 值            | 說明                      |
| ------------------------------ | ------------- | ------------------------- |
| `entry_start_time`           | 09:05 / 09:09 | 進場開始時間 (版本 A / B) |
| `entry_end_time`             | 13:00         | 進場結束時間              |
| `require_ask_gt_bid`         | true          | 要求五檔委賣 > 委買       |
| `position_cash`              | 10,000,000    | 每檔部位資金              |
| `max_entry_price`            | 1,000         | 最高進場價                |
| `disposition_stocks_enabled` | false         | 不進場處置股              |

### 大量搓合 (版本 B 專用)

| 參數                                | 值   | 說明                |
| ----------------------------------- | ---- | ------------------- |
| `massive_matching_enabled`        | true | 啟用大量搓合過濾    |
| `massive_matching_window`         | 1    | 滑動窗口 (秒)       |
| `use_dynamic_liquidity_threshold` | true | 使用動態門檻        |
| `price_change_limit_enabled`      | true | 漲幅上限過濾        |
| `price_change_limit_pct`          | 8.5  | 漲幅上限 (%)        |
| `interval_pct_filter_enabled`     | true | 急拉濾網            |
| `interval_pct_threshold`          | 3.0  | 5 分鐘漲幅上限 (%)  |
| `ratio_entry_enabled`             | true | 量比濾網            |
| `ratio_entry_threshold`           | 3.0  | ratio_15s_300s 門檻 |

### 出場

| 參數                             | 值    | 說明                |
| -------------------------------- | ----- | ------------------- |
| `stop_loss_pct`                | 1.2   | 百分比停損 (%)      |
| `exit_time_limit`              | 13:00 | 時間出場            |
| `bailout_enabled`              | false | Bailout (停用)      |
| `trailing_low_enabled`         | true  | Trailing Low        |
| `trailing_low_require_tp_fill` | false | 不需停利觸發即可 TL |
| `group_rank_exit_enabled`      | false | 族群排名出場 (停用) |

### 停利

| 參數                       | 值                    | 說明                |
| -------------------------- | --------------------- | ------------------- |
| `mode_e_tp_base`         | entry_price           | 以進場價為基準      |
| `take_profit_pcts`       | [0.008, 0.016, 0.024] | +0.8%, +1.6%, +2.4% |
| `take_profit_ratios`     | [0.25, 0.25, 0.25]    | 每檔 25%            |
| `reserve_limit_up_ratio` | 0.25                  | 漲停單 25%          |

---

## 附錄：關鍵程式對應

| 邏輯        | 檔案                            | 重要方法                                                     |
| ----------- | ------------------------------- | ------------------------------------------------------------ |
| Tick 主迴圈 | `Core/BacktestLoop.cs`        | `ProcessTick()`                                            |
| 族群篩選    | `Strategy/StrongGroup.cs`     | `OnTick()`, `UpdateGroupStates()`, `RankMembers()`     |
| Mode E 選取 | `Strategy/StrongGroup.cs`     | `GetSelectedMembersModeE()`                                |
| 進場執行    | `Strategy/OrderTrigger.cs`    | `TryEntry()`, `PlaceModeETakeProfitOrders()`             |
| 出場邏輯    | `Strategy/ExitLogic.cs`       | `CheckExit()`, `CheckTrailingLowExit()`                  |
| 大盤過濾    | `Strategy/MarketFilter.cs`    | `UpdateTick()`, `ShouldSkipTick()`                       |
| 部位管理    | `Strategy/PositionManager.cs` | `OpenPosition()`, `ClosePosition()`, `ForceCloseAll()` |
| 大量搓合    | `Core/BacktestLoop.cs`        | `CheckMassiveMatchingFilters()`                            |
| 滑動窗口    | `Core/Models/IndexData.cs`    | `UpdateMassiveMatching()`                                  |
| 跳動單位    | `Strategy/TickSizeHelper.cs`  | `GetTickSize()`, `CeilToTick()`, `CalculateLimitUp()`  |
| 資料載入    | `Strategy/DataLoader.cs`      | `LoadGroupsFromScreeningCsv()`, `LoadPerStockTickData()` |
