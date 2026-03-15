# 設定參數參考

所有參數定義在 `Strategy/ConfigLoader.cs` 的 `GetDefaultConfig()` 中，由 YAML 的 4 個 section 覆蓋。

## strategy section — 大盤過濾

| 參數 | 預設值 | 說明 |
|------|--------|------|
| market_rally_disable_threshold | 0.2 | 0050 漲幅 >= 此值時停用策略 (09:15 檢查) |
| market_open_min_chg | 0.0 | 0050 開盤漲幅最低門檻 |

## signal_a section — Signal A 進場訊號

| 參數 | 預設值 | 說明 |
|------|--------|------|
| signal_a_enabled | true | 是否啟用 Signal A (false = Mode E) |
| vwap_near_ratio | 1.008 | Phase 1: price/vwap <= 此值 → near VWAP |
| bounce_ratio | 0.008 | Phase 2: 反彈幅度 >= 此值 → 觸發進場 |
| entry_start_time | 09:04:00 | Signal A 進場起始時間 |
| entry_end_time | 09:25:00 | Signal A 進場截止時間 |
| trade_zone_max_increase_ratio | 0.085 | 漲幅上限 (超過不觸發) |
| pre_condition_start_time | 09:04:00 | Pre-condition 檢查起始時間 |
| pre_condition_vwap_ratio | 0.997 | price <= vwap × 此值 → 永久封鎖 |

## strong_group section — 族群篩選

### 族群門檻

| 參數 | 預設值 | 說明 |
|------|--------|------|
| strong_group_enabled | true | 是否啟用族群篩選 |
| member_min_month_trading_val | 200,000,000 | 成員月均量門檻 (2億; Mode E 用 3億) |
| group_min_month_trading_val | 3,000,000,000 | 族群月均量門檻 (30億) |
| group_min_avg_pct_chg | 0.01 | 族群平均漲幅門檻 (Mode E 用 -999 = 不限) |
| group_min_val_ratio | 1.2 | 族群量比門檻 (Mode E 用 0.0 = 不限) |
| is_weighted_avg | false | 族群平均漲幅是否加權 |

### 排名與選取

| 參數 | 預設值 | 說明 |
|------|--------|------|
| group_valid_top_n | 20 | 有效族群上限 |
| top_group_rank_threshold | 10 | 前 N 名為 "頂級" 族群 |
| top_group_max_select | 1 | 頂級族群每族選取成員數 |
| top_group_min_select | 1 | 頂級族群最少選取數 |
| normal_group_max_select | 1 | 一般族群每族選取成員數 |
| normal_group_min_select | 1 | 一般族群最少選取數 |
| entry_min_vwap_pct_chg | 0.04 | 成員 VWAP 漲幅最低門檻 |
| require_raw_m1 | true | 是否要求 raw 排名第一 |

### 條件過濾

| 參數 | 預設值 | 說明 |
|------|--------|------|
| member_cond1_enabled | false | 成員條件 1 (量比 or 大額) |
| member_cond2_enabled | false | 成員條件 2 (漲幅>2% + VWAP>1%) |
| member_cond4_enabled | false | 成員條件 4 (VWAP 漲幅門檻) |
| member_strong_vol_ratio | 1.5 | 條件 1: 量比門檻 |
| member_strong_trading_val | 2,000,000,000 | 條件 1: 大額門檻 |
| member_vwap_pct_chg_threshold | 0.03 | 條件 4: VWAP 漲幅門檻 |
| group_vol_ratio_exempt_threshold | 30,000,000,000 | 條件 1: 超大族群豁免 |
| exclude_prev_limit_up_from_rank | false | 排名時排除前日漲停 |
| exclude_disposition_from_rank | false | 排名時排除處置股 |

### Mode E 動態選取

| 參數 | 預設值 | 說明 |
|------|--------|------|
| mode_e_enabled | false | 啟用 Mode E 動態成員選擇 |
| mode_e_large_group_threshold | 5 | 大族群定義 (成員數 >=) |
| mode_e_large_group_select | 3 | 大族群選取數 |
| mode_e_small_group_select | 2 | 小族群選取數 |
| mode_e_limit_up_cascade | true | 漲停跳過，cascade 至下一名 |
| mode_e_max_limit_up_skip | 5 | 最多跳過漲停數 |

## order section — 進出場

### 進場

| 參數 | 預設值 | 說明 |
|------|--------|------|
| position_cash | 10,000,000 | 每檔部位資金 (1000萬) |
| disposition_stocks_enabled | true | 是否允許處置股進場 |
| filter_prev_day_limit_up | true | 過濾前日漲停股 |
| max_entry_price | 1000 | 最大進場價 |
| entry_time_limit | 13:00:00 | 進場截止時間 |
| entry_start_time | 09:05:00 | Mode E 進場起始時間 |
| entry_end_time | 09:20:00 | Mode E 進場截止時間 |

### 出場

| 參數 | 預設值 | 說明 |
|------|--------|------|
| mode_e_exit | false | 啟用 Mode E 出場 (entry price based TP) |
| stop_loss_enabled | true | 啟用停損 |
| stop_loss_pct | 0 | 百分比停損 (>0 時使用, 例: 1.2 = -1.2%) |
| stop_loss_ratio_a | 0.995 | VWAP 停損比率 (pct=0 時使用) |
| bailout_enabled | true | 啟用 bailout (Mode E 停用) |
| bailout_ratio | 0.8 | Bailout 價位 = entryDayHigh × ratio |
| exit_time_limit | 13:20:00 | 時間出場 |

### 停利

| 參數 | 預設值 | 說明 |
|------|--------|------|
| take_profit_splits | 3 | TP 檔數 (原始模式) |
| take_profit_pcts | [0.01, 0.02, 0.03] | TP 百分比 |
| take_profit_ratios | [] | TP 股數比例 (Mode E: [0.2, 0.2, 0.2]) |
| reserve_limit_up_splits | 2 | 漲停保留份數 (原始模式) |
| reserve_limit_up_ratio | 0.4 | 漲停保留比例 (Mode E: 40%) |
| mode_e_tp_base | "" | TP 基準 ("entry_price") |

### 族群排名出場 (BacktestLoop 讀取)

| 參數 | 預設值 | 說明 |
|------|--------|------|
| group_rank_exit_enabled | false | 啟用族群排名下降出場 |
| group_rank_exit_threshold | 3 | 排名門檻 (>= 此值且比進場時差) |

## 路徑設定

| 參數 | 預設值 |
|------|--------|
| output_path | D:\回測結果 |
| group_csv_path | group.csv |
| tick_data_base_path | D:\feature_data\feature |

---

## Mode E 完整設定範例

```yaml
strategy:
  market_rally_disable_threshold: 0.2
  market_open_min_chg: 0.0

signal_a:
  signal_a_enabled: false

strong_group:
  strong_group_enabled: true
  member_min_month_trading_val: 300000000      # 3億
  group_min_month_trading_val: 3000000000      # 30億
  group_min_avg_pct_chg: -999                  # 不限
  group_min_val_ratio: 0.0                     # 不限
  is_weighted_avg: false
  group_valid_top_n: 5                         # 前5族群 (可改3)
  top_group_rank_threshold: 5
  top_group_max_select: 3
  normal_group_max_select: 2
  entry_min_vwap_pct_chg: 0.0
  require_raw_m1: false
  member_cond1_enabled: false
  member_cond2_enabled: false
  member_cond4_enabled: false
  exclude_prev_limit_up_from_rank: false
  exclude_disposition_from_rank: true
  mode_e_enabled: true
  mode_e_large_group_threshold: 5
  mode_e_large_group_select: 3
  mode_e_small_group_select: 2
  mode_e_limit_up_cascade: true
  mode_e_max_limit_up_skip: 5

order:
  position_cash: 10000000
  disposition_stocks_enabled: false
  filter_prev_day_limit_up: false
  max_entry_price: 1000
  entry_time_limit: "09:20:00"
  mode_e_exit: true
  stop_loss_enabled: true
  stop_loss_pct: 1.2
  bailout_enabled: false
  exit_time_limit: "13:00:00"
  mode_e_tp_base: "entry_price"
  take_profit_splits: 4
  take_profit_pcts: [0.012, 0.016, 0.02]
  take_profit_ratios: [0.2, 0.2, 0.2]
  reserve_limit_up_splits: 1
  reserve_limit_up_ratio: 0.4
  entry_start_time: "09:05:00"
  entry_end_time: "09:20:00"
  # 族群排名出場 (可選)
  group_rank_exit_enabled: false
  group_rank_exit_threshold: 3
```
