# Config Optimizer Agent

## 用途
優化回測參數，調整 Bo_v2.yaml 設定。

## 可調整的關鍵參數

### 進場參數
- `ratio_entry_threshold` — Ratio 門檻（影響進場頻率）
- `massive_matching_amount` — 巨額撮合門檻
- `interval_pct_threshold` — 區間漲幅門檻

### 出場參數
- `strategy_b_stop_loss_ticks_small/large` — 停損格數
- `trailing_stop.levels` — 拖曳停利層級

### 優化方法
1. 單變數掃描：固定其他參數，掃描目標參數
2. 回測結果比較：勝率、平均損益、最大回撤
