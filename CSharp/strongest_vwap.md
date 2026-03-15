# 台股盤中動量交易系統 - 完整進出場邏輯

## 系統概觀

每筆 tick（逐筆成交）進來時，按以下順序處理：

```

tick 進入

  → 大盤過濾（0050）

  → 計算 IndexData（VWAP、日高低）

  → 出場檢查（已持倉的股票）

  → 強勢篩選（StrongGroup）

  → 訊號評估（Signal A）

  → 進場下單（Order.trigger）

```

---

## 一、大盤層級過濾

在任何個股邏輯之前，先檢查 0050（元大台灣50 ETF）判斷大盤狀態。

### 1.1 開盤漲幅檢查（09:00 觸發）

```

0050_open_chg = (0050 在 09:00 的成交價 - 0050 前收盤價) / 0050 前收盤價


如果 0050_open_chg < market_open_min_chg → 當日停止交易

```

| 參數 | 當前值 | 說明 |

|------|--------|------|

| `market_open_min_chg` | 0 | 0050 開盤最低漲幅，0=不過濾 |

### 1.2 Tick 過濾

- 忽略 `tradeCode != 1` 的成交（非一般交易）
- 忽略代碼以 `"00"` 開頭的 ETF/指數（僅用於捕捉 0050 價格）

---

## 二、IndexData 計算（每筆 tick）

對每個個股，每筆 tick 即時計算以下數據：

| 欄位 | 公式 | 說明 |

|------|------|------|

| `vwap` | Σ(成交價 × 成交量) / Σ(成交量) | 成交量加權平均價（開盤至今） |

| `day_high` | max(所有成交價) | 當日最高價 |

| `day_low` | min(所有成交價) | 當日最低價 |

**價格單位**：所有價格以 `long long` 儲存，= 實際價格 × 10000（例如 50.00 元 → 500000）

---

## 三、強勢族群篩選（StrongGroup）

### 3.1 族群定義

族群成員定義在 `group.csv`：

```

族群名稱,股票代號,股票名稱

散熱,3017,奇鋐

散熱,2379,瑞昱

CPO,4919,新唐

...

```

一支股票可以同時屬於多個族群。

### 3.2 族群層級篩選

每筆 tick 更新族群狀態，族群必須同時滿足以下 4 個條件才有效：

| 條件 | 公式 | 參數 | 當前值 |

|------|------|------|--------|

| 成員月均成交值 | 該股票 20 日平均成交值 ≥ 門檻 | `member_min_month_trading_val` | 2 億 |

| 族群月均成交值 | 族群所有有效成員的 20 日平均成交值總和 ≥ 門檻 | `group_min_month_trading_val` | 30 億 |

| 族群平均漲幅 | 族群成員的平均股價漲幅 > 門檻 | `group_min_avg_pct_chg` | 0.01 (1%) |

| 族群成交值比 | 今日累計成交值 / 月均成交值 ≥ 門檻 | `group_min_val_ratio` | 1.2 |

**族群平均漲幅計算**：

-`is_weighted_avg=false`（當前）：算術平均（每個成員等權重）

-`is_weighted_avg=true`：按成交值加權平均

**漲幅公式**：`(最新成交價 - 前收盤價) / 前收盤價`

### 3.3 族群排名

通過篩選的族群，依照「族群平均漲幅」由高到低排名。

| 參數 | 當前值 | 說明 |

|------|--------|------|

| `group_valid_top_n` | 20 | 只處理排名前 20 的族群，其餘忽略 |

### 3.4 成員排名（VWAP 漲幅排名）

在每個有效族群中，成員依照 **VWAP 漲幅** 由高到低排名。

**VWAP 漲幅**：`(該股票當前 VWAP - 前收盤價) / 前收盤價`

排名有兩套：

| 排名 | 過濾條件 | 用途 |

|------|----------|------|

| **Raw Rank**（原始排名） | 僅排除：漲停鎖死、漲幅≥8.5%、月均成交值不足 | 用於 `require_raw_m1` 判斷 |

| **Filtered Rank**（篩選排名） | 額外排除：cond1/2/4 不通過的、處置股、前日漲停股 | 用於決定最終是否進場 |

**成員排除條件**（從 Filtered Rank 中移除）：

- 漲停鎖死（`isLimitUpLocked`）
- VWAP 漲幅 ≥ 8.5%
- 處置股（security == "RR"，如果 `exclude_disposition_from_rank=true`）
- 前日漲停（如果 `exclude_prev_limit_up_from_rank=true`）

### 3.5 成員條件（cond1/2/4）

進入 Filtered Rank 之前，成員必須通過啟用的條件：

| 條件 | 啟用參數 | 公式 | 當前狀態 |

|------|----------|------|----------|

| **cond1**：量能條件 | `member_cond1_enabled` | 累計成交量/月均量 ≥ 1.5 **或** 月均成交值 > 20億 **或** 族群成交值豁免 | **關閉** |

| **cond2**：價格動能 | `member_cond2_enabled` | 股價漲幅 > 2% **且** VWAP漲幅 > 1% | **關閉** |

| **cond4**：VWAP 門檻 | `member_cond4_enabled` | VWAP漲幅 > `member_vwap_pct_chg_threshold` | **關閉** |

> 當前三個條件全部關閉（`false`），等於不額外過濾，直接用排名。

**族群量能豁免**：如果族群月均成交值總和 > `group_vol_ratio_exempt_threshold`（300億），該族群成員免除 cond1。

### 3.6 選股邏輯

每個族群根據排名選取 N 個成員：

| 族群類型 | 條件 | 最多選取 | 最少選取 |

|----------|------|----------|----------|

| **頂級族群** | 族群排名 ≤ `top_group_rank_threshold` (10) | `top_group_max_select` = 1 | `top_group_min_select` = 1 |

| **一般族群** | 其他族群 | `normal_group_max_select` = 1 | `normal_group_min_select` = 1 |

被選中的成員還必須通過：

```

1. VWAP 漲幅 ≥ entry_min_vwap_pct_chg (4%)

2. 如果 require_raw_m1=true → Raw Rank 必須 = 1（原始排名第一）

```

| 參數 | 當前值 | 說明 |

|------|--------|------|

| `entry_min_vwap_pct_chg` | 0.04 (4%) | VWAP 漲幅最低門檻 |

| `require_raw_m1` | true | 必須是原始排名 M1 才能進場 |

### 3.7 StrongGroup 輸出

當一支股票通過所有篩選，`strongGroup.on_tick()` 回傳 `true`，並記錄：

```

MatchInfo {

    group_name:      族群名稱（如 "散熱"）

    group_rank:      族群在所有族群中的排名（1-based）

    member_rank:     股票在族群 Filtered Rank 中的排名（1-based）

    raw_member_rank: 股票在族群 Raw Rank 中的排名（1-based）

    m1_symbol:       該族群 Filtered Rank 第一名的股票代號

}

```

---

## 四、Signal A 訊號模型

Signal A 是一個兩階段模型：**接近均線 → 反彈進場**

### 4.1 前置條件（Pre-Condition）

在進入訊號判斷前，先檢查前置條件：

```

如果 當前時間 < pre_condition_start_time (09:04)：

    → 通過（不檢查）


如果 當前時間 ≥ 09:04：

    如果 成交價 ≤ VWAP × pre_condition_vwap_ratio (0.997)：

        → 永久禁止（此股票今日不再觸發 Signal A）

    否則：

        → 通過

```

| 參數 | 當前值 | 說明 |

|------|--------|------|

| `pre_condition_start_time` | 09:04:00 | 開始檢查前置條件的時間 |

| `pre_condition_vwap_ratio` | 0.997 | 價格低於 VWAP × 0.997 = 禁止 |

**關鍵**：一旦 forbidden 被觸發，該股票今天不可能再進場。

### 4.2 進場時間窗口

```

entry_start_time (09:04) ≤ 當前時間 < entry_end_time (09:25)

```

| 參數 | 當前值 | 說明 |

|------|--------|------|

| `entry_start_time` | 09:04:00 | 最早進場時間 |

| `entry_end_time` | 09:25:00 | 最晚進場時間 |

### 4.3 最大漲幅限制

```

(成交價 - 前收盤價) / 前收盤價 > trade_zone_max_increase_ratio (8.5%)

→ 不進場（漲太多了，追高風險）

```

### 4.4 兩階段模型

**MatchType 前提**：Signal A 只在 `MatchType != None` 時評估。也就是說，股票必須先通過 StrongGroup 篩選。如果 StrongGroup 回傳 false，Signal A 重置 `near_vwap` 狀態。

#### 階段一：偵測接近 VWAP

```

如果 成交價 / VWAP ≤ vwap_near_ratio (1.008)：

    near_vwap = true

    low_since_near = 當前成交價（開始記錄最低價）

```

**意義**：股價回到 VWAP 附近（在 VWAP 上方 0.8% 以內），開始追蹤。

#### 階段二：等待反彈

```

持續更新 low_since_near = min(low_since_near, 當前成交價)


bounce = (當前成交價 - low_since_near) / low_since_near


如果 bounce ≥ bounce_ratio (0.008 = 0.8%)：

    → 觸發進場！（triggered = true）

```

**意義**：碰到 VWAP 附近後反彈 0.8%，確認支撐有效，進場。

| 參數 | 當前值 | 說明 |

|------|--------|------|

| `vwap_near_ratio` | 1.008 | 判定「接近 VWAP」的比值（≤ VWAP × 1.008） |

| `bounce_ratio` | 0.008 | 從最低點反彈 0.8% 觸發進場 |

### 4.5 Signal A 完整流程圖

```

每筆 tick:


  StrongGroup 通過？ ──No──→ 重置 near_vwap，跳過

       │

      Yes

       │

  已經觸發過？ ──Yes──→ 跳過（每支股票只進場一次）

       │

      No

       │

  在時間窗口內？(09:04~09:25) ──No──→ 跳過

       │

      Yes

       │

  前置條件通過？ ──No──→ forbidden=true，永久跳過

       │

      Yes

       │

  漲幅 < 8.5%？ ──No──→ 跳過

       │

      Yes

       │

  ┌─ 階段一：price/VWAP ≤ 1.008？

  │    Yes → near_vwap=true, low_since_near=price

  │

  └─ 階段二（near_vwap=true 後）：

       更新 low_since_near

       bounce = (price - low) / low

       bounce ≥ 0.008？

         Yes → ★ 進場觸發 ★

```

---

## 五、進場下單（Order.trigger）

Signal A 觸發後，進入下單流程。

### 5.1 進場前過濾

依序檢查，任一不通過就放棄進場：

| 順序 | 條件 | 參數 | 當前值 |

|------|------|------|--------|

| 1 | 進場時間限制 | `entry_time_limit` | 13:00:00 |

| 2 | 前日漲停過濾 | `filter_prev_day_limit_up` | true |

| 3 | 處置股過濾（波動暫停） | `disposition_stocks_enabled` | true |

| 4 | 已持有該股票 | - | 不重複進場 |

| 5 | 最高進場股價 | `max_entry_price` | 1000 元 |

### 5.2 進場價格

```

進場價 = ask[0].Price（委賣第一檔）

如果 ask[0] 不存在 → 使用最新成交價

```

### 5.3 部位計算

```

買進股數 = position_cash / 進場價

```

| 參數 | 當前值 | 說明 |

|------|--------|------|

| `position_cash` | 10,000,000 | 每筆進場的固定金額（1000萬） |

**範例**：股價 50 元 → 買進 200,000 股 (= 10,000,000 / 50)

### 5.4 停利掛單（進場時立即掛出）

部位分成 **5 等份**（3 筆停利 + 2 筆漲停預留）：

```

total_splits = take_profit_splits + reserve_limit_up_splits = 3 + 2 = 5

每份數量 = 總股數 / 5

```

**停利單（3 筆）- 百分比模式**：

| 筆次 | 掛單價格公式 | 範例（day_high=100） |

|------|-------------|---------------------|

| 第 1 筆 | day_high × (1 + 1%) = day_high × 1.01，向上取整至有效 tick | 101.0 |

| 第 2 筆 | day_high × (1 + 2%) = day_high × 1.02，向上取整至有效 tick | 102.0 |

| 第 3 筆 | day_high × (1 + 3%) = day_high × 1.03，向上取整至有效 tick | 103.0 |

**漲停預留（2 筆）**：

| 筆次 | 掛單價格 |

|------|---------|

| 第 4 筆 | 漲停價 |

| 第 5 筆 | 漲停價 |

**價格上限**：所有停利掛單價格不能超過漲停價，超過的會被調整為漲停價。

**向上取整到有效 tick**：例如計算出 101.3 元，但該價位的 tick 是 0.5 元，則向上取整為 101.5 元。

| 參數 | 當前值 | 說明 |

|------|--------|------|

| `take_profit_splits` | 3 | 停利掛單筆數 |

| `take_profit_pcts` | 0.01, 0.02, 0.03 | 各筆相對 day_high 的百分比偏移 |

| `reserve_limit_up_splits` | 2 | 預留給漲停的筆數 |

### 5.5 台股 Tick 表（跳動單位）

停利掛單價格的取整依據：

| 股票類型 | 價格區間 | Tick（跳動點） |

|----------|---------|---------------|

| 股票 | ≥ 1000 元 | 5 元 |

| 股票 | 500~999 元 | 1 元 |

| 股票 | 100~499 元 | 0.5 元 |

| 股票 | 50~99 元 | 0.1 元 |

| 股票 | 10~49 元 | 0.05 元 |

| 股票 | < 10 元 | 0.01 元 |

> 注意：權證、ETF、可轉債有不同的 tick 表，但目前策略只交易股票。

---

## 六、出場邏輯

持倉後每筆 tick 依序檢查出場條件，優先級由高到低：

### 6.1 停損（最高優先）

```

如果 成交價 ≤ 進場時VWAP × stop_loss_ratio_a

→ 取消所有停利掛單，全部市價賣出

```

| 參數 | 當前值 | 說明 |

|------|--------|------|

| `stop_loss_ratio_a` | 0.995 | 跌破 VWAP 的 0.5% 就停損 |

**計算範例**：

- 進場時 VWAP = 50.00 元
- 停損觸發價 = 50.00 × 0.995 = 49.75 元
- 如果進場價是 50.50 元，實際虧損 ≈ (49.75 - 50.50) / 50.50 = -1.49%

**關鍵**：停損基準是**進場當下的 VWAP**，不是進場價。由於進場價通常高於 VWAP（平均高 ~1.2%），實際停損幅度約 -1.5% ~ -2.5%。

### 6.2 時間出場

```

如果 當前時間 ≥ exit_time_limit (13:20)

→ 取消所有停利掛單，全部市價賣出

```

| 參數 | 當前值 | 說明 |

|------|--------|------|

| `exit_time_limit` | 13:20:00 | 超過此時間強制平倉 |

### 6.3 停利成交

```

如果 成交價 ≥ 停利掛單價格

→ 該筆停利掛單成交，部分出場

→ 標記 profitTaken = true

```

停利是部分成交機制：

- 5 筆掛單各自獨立判斷
- 價格到哪筆就成交哪筆
- 全部成交 → 出場原因 = "takeProfit"
- 部分成交後繼續持有剩餘部位

### 6.4 Bailout（停利後回撤保護）

```

前提：profitTaken = true（已有停利成交）


如果 成交價 ≤ 進場時day_high × bailout_ratio (0.8)

→ 取消剩餘掛單，全部市價賣出

```

| 參數 | 當前值 | 說明 |

|------|--------|------|

| `bailout_ratio` | 0.8 | 跌回 day_high 的 80%（極端回撤保護） |

**意義**：部分停利成交後，如果股價劇烈回撤，保護剩餘部位不要虧太多。

### 6.5 出場優先級

```

每筆 tick 的檢查順序：

  1. stopLoss     → 觸發就立即全部出場

  2. timeExit     → 觸發就立即全部出場

  3. takeProfit   → 可能部分成交，繼續持有

  4. bailout      → 已停利後才檢查，觸發就全部出場

```

---

## 七、完整進場條件檢查清單

一支股票要進場，**所有**以下條件必須同時滿足：

### 大盤層級

- [ ] 0050 開盤漲幅 ≥ `market_open_min_chg` (0)
- [ ] 0050 在 09:15 的漲幅 < `market_rally_disable_threshold` (20%)

### 族群層級

- [ ] 股票屬於至少一個族群（在 group.csv 中）
- [ ] 該族群的成員月均成交值 ≥ 2 億
- [ ] 該族群的總月均成交值 ≥ 30 億
- [ ] 該族群的平均漲幅 > 1%
- [ ] 該族群的成交值比 > 1.2
- [ ] 該族群在所有族群中排名前 20

### 成員層級

- [ ] 股票的 VWAP 漲幅 ≥ 4%（`entry_min_vwap_pct_chg`）
- [ ] 股票在原始排名中是 M1（第一名）（`require_raw_m1`）
- [ ] 股票未漲停鎖死
- [ ] 股票漲幅 < 8.5%
- [ ] 股票在 Filtered Rank 中被選中（排名 ≤ `max_select`）

### Signal A 層級

- [ ] 當前時間在 09:04 ~ 09:25 之間
- [ ] 前置條件通過（價格未跌破 VWAP × 0.997）
- [ ] 股價漲幅 < 8.5%
- [ ] 曾接近 VWAP（price/VWAP ≤ 1.008）
- [ ] 從接近後的最低點反彈 ≥ 0.8%

### 下單層級

- [ ] 當前時間 < 13:00（`entry_time_limit`）
- [ ] 非前日漲停股
- [ ] 非處置股波動暫停
- [ ] 未持有該股票
- [ ] 股價 ≤ 1000 元（`max_entry_price`）

---

## 八、監視網頁建議追蹤的即時數據

### 8.1 大盤狀態

| 欄位 | 數據來源 | 更新頻率 |

|------|---------|---------|

| 0050 前收盤價 | Format1 | 開盤前載入 |

| 0050 當前價 | 逐筆 tick | 每筆 |

| 0050 漲幅 % | (當前價 - 前收) / 前收 | 每筆 |

| 策略狀態 | enabled / disabled | 09:00, 09:15 判斷 |

### 8.2 族群排名（每筆 tick 更新）

| 欄位 | 說明 |

|------|------|

| 族群名稱 | group.csv 定義 |

| 族群排名 | 依平均漲幅排序 |

| 族群平均漲幅 | 成員等權重平均 |

| 族群成交值比 | 今日累計 / 月均 |

| 有效成員數 | 通過所有過濾條件的成員 |

| M1 股票代號 | Filtered Rank 第一名 |

| M1 VWAP 漲幅 | M1 的 VWAP 變化率 |

### 8.3 個股狀態（被追蹤的股票）

| 欄位 | 說明 |

|------|------|

| 股票代號 | |

| 所屬族群 | 可能多個 |

| 最新成交價 | |

| VWAP | 成交量加權均價 |

| price / VWAP | 價格相對 VWAP 的位置 |

| VWAP 漲幅 | (VWAP - 前收) / 前收 |

| 日最高價 | |

| Raw Rank | 原始排名 |

| Filtered Rank | 篩選後排名 |

| near_vwap | 是否已接近 VWAP |

| low_since_near | 接近後的最低價 |

| bounce | 當前反彈幅度 |

| forbidden | 是否已被禁止 |

| triggered | 是否已觸發進場 |

### 8.4 持倉狀態

| 欄位 | 說明 |

|------|------|

| 股票代號 | |

| 進場價 | ask[0] 或成交價 |

| 進場時 VWAP | 停損基準 |

| 進場時 Day High | 停利基準 |

| 停損價 | VWAP × 0.995 |

| 停利掛單 1~3 | day_high × 1.01/1.02/1.03 |

| 漲停掛單 4~5 | 漲停價 |

| 各筆掛單狀態 | 未成交 / 已成交 |

| 未實現損益 | (當前價 - 進場價) × 持股 |

| profitTaken | 是否已有停利成交 |

---

## 九、當前參數總覽

```ini

[Strategy]

market_rally_disable_threshold = 0.2    # 0050 漲超 20% 停止

market_open_min_chg = 0                 # 不過濾開盤漲幅


[SignalA]

enabled = true

vwap_near_ratio = 1.008                 # 接近 VWAP 判定（≤ VWAP × 1.008）

bounce_ratio = 0.008                    # 反彈 0.8% 進場

entry_start_time = 09:04:00             # 進場窗口開始

entry_end_time = 09:25:00               # 進場窗口結束

trade_zone_max_increase_ratio = 0.085   # 漲幅上限 8.5%

pre_condition_start_time = 09:04:00     # 前置條件開始時間

pre_condition_vwap_ratio = 0.997        # 價格 > VWAP × 0.997


[StrongGroup]

enabled = true

member_min_month_trading_val = 200000000      # 成員月均成交值 ≥ 2億

group_min_month_trading_val = 3000000000      # 族群月均成交值 ≥ 30億

group_min_avg_pct_chg = 0.01                  # 族群平均漲幅 > 1%

group_min_val_ratio = 1.2                     # 族群成交值比 > 1.2

member_strong_vol_ratio = 1.5                 # (cond1, 關閉)

member_strong_trading_val = 2000000000        # (cond1, 關閉)

top_group_rank_threshold = 10                 # 前10名為頂級族群

top_group_max_select = 1                      # 頂級族群最多選1人

normal_group_max_select = 1                   # 一般族群最多選1人

member_vwap_pct_chg_threshold = 0.03          # (cond4, 關閉)

group_valid_top_n = 20                        # 只處理前20族群

is_weighted_avg = false                       # 族群漲幅用等權重

group_vol_ratio_exempt_threshold = 30000000000 # 族群成交值>300億豁免cond1

exclude_prev_limit_up_from_rank = false

exclude_disposition_from_rank = false

member_cond1_enabled = false                  # 量能條件（關閉）

member_cond2_enabled = false                  # 動能條件（關閉）

member_cond4_enabled = false                  # VWAP條件（關閉）

entry_min_vwap_pct_chg = 0.04                # VWAP漲幅最低4%

require_raw_m1 = true                         # 必須是原始排名M1


[SignalB]

enabled = false                               # Signal B 停用


[StrongSignal]

enabled = false                               # 強勢個股篩選停用


[Order]

position_cash = 10000000                      # 每筆進場 1000萬

disposition_stocks_enabled = true             # 過濾處置股

filter_prev_day_limit_up = true               # 過濾前日漲停

stop_loss_ratio_a = 0.995                     # 停損：VWAP × 0.995

stop_loss_ratio_b = 0.993                     # (Signal B 用，停用)

bailout_ratio = 0.8                           # 停利後回撤保護：DH × 0.8

entry_time_limit = 13:00:00                   # 最晚進場時間

exit_time_limit = 13:20:00                    # 強制平倉時間

take_profit_splits = 3                        # 停利 3 筆

take_profit_pcts = 0.01, 0.02, 0.03           # DH+1%, DH+2%, DH+3%

reserve_limit_up_splits = 2                   # 漲停預留 2 筆

max_entry_price = 1000                        # 最高進場股價 1000 元

```
