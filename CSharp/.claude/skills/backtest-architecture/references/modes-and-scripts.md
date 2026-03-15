# 回測模式與批次腳本

## 執行模式

### Single Mode

單日回測，用於除錯和驗證。

```bash
cd D:\03_預估量相關資量\CSharp\StrongestVwap
dotnet run -- --mode single --date 2026-01-14 \
  --config "configs/mode_e_group_screening.yaml" \
  --screening_csv "C:\Users\User\Documents\_02_bt\Backtest_tick_module\screening_results.csv" \
  --tick_data "D:\feature_data\feature"
```

### Batch Mode

多日批次回測，自動匯總 batch_trades.parquet。

```bash
dotnet run -- --mode batch \
  --dates 2025-11-03 2025-11-04 2025-11-05 \
  --config "configs/mode_e_group_screening.yaml" \
  --screening_csv "path/to/screening_results.csv" \
  --tick_data "D:\feature_data\feature" \
  --output_path "D:\C#_backtest\my_test"
```

### Replay Mode

秒級族群回放，生成 JSON + HTML 可視化。

```bash
dotnet run -- --mode replay --date 2026-03-06 \
  --screening_csv "path/to/screening_results.csv" \
  --close_parquet "D:\03_預估量相關資量\CSharp\close.parquet" \
  --tick_data "D:\feature_data\feature" \
  --output_path "D:\replay_output"
```

---

## 策略模式對照

| 特性 | 原始模式 (Signal A) | Mode E |
|------|---------------------|--------|
| signal_a_enabled | true | false |
| 進場訊號 | VWAP 觸碰→反彈 | DayHigh 突破 |
| 進場時間 | 09:04~09:25 | 09:05~09:20 |
| TP 基準 | DayHigh | Entry Price |
| TP 分配 | 均分 (3+2漲停) | 比例 (20/20/20/40漲停) |
| 停損 | VWAP × 0.995 | Entry × (1-1.2%) |
| Bailout | 有 (DayHigh × 0.8) | 停用 |
| 族群選取 | require_raw_m1 + 固定 max_select | 動態 cascade |
| 成員門檻 | 2億 | 3億 |
| 處置股 | 可進場 | 排除 |
| 族群排名出場 | 無 | 可選 |

---

## 已有 YAML 設定檔

### configs/mode_e_group_screening.yaml (Config A)

基準 Mode E 設定：TP 1.2/1.6/2.0%, Top 5 族群, 無排名出場

### configs/mode_e_B.yaml

TP 改為 0.8/1.6/2.4%, Top 5, 無排名出場

### configs/mode_e_C.yaml

TP 1.2/1.6/2.0%, Top 3 族群 (group_valid_top_n=3, top_group_rank_threshold=3), 無排名出場

### configs/mode_e_D.yaml

TP 0.8/1.6/2.4%, Top 3, 無排名出場

### configs/mode_e_E.yaml

TP 1.2/1.6/2.0%, Top 3, 族群排名出場 (group_rank_exit_enabled=true, threshold=3)

### configs/mode_e_F.yaml

TP 0.8/1.6/2.4%, Top 3, 族群排名出場

---

## 批次腳本

### run_modeE_grid.ps1

6 組 Mode E 網格回測 × 63 日期 (2025-11-03 ~ 2026-01-31)。

```powershell
$configs = @(
    @{ Name="A_tp120_top5";        Config="configs/mode_e_group_screening.yaml" },
    @{ Name="B_tp080_top5";        Config="configs/mode_e_B.yaml" },
    @{ Name="C_tp120_top3";        Config="configs/mode_e_C.yaml" },
    @{ Name="D_tp080_top3";        Config="configs/mode_e_D.yaml" },
    @{ Name="E_tp120_top3_rankExit"; Config="configs/mode_e_E.yaml" },
    @{ Name="F_tp080_top3_rankExit"; Config="configs/mode_e_F.yaml" }
)

# 輸出: D:\C#_backtest\modeE_grid\{Name}\
```

執行: `powershell -ExecutionPolicy Bypass -File run_modeE_grid.ps1`

---

## 網格回測結果 (2026-03-07)

55 交易日, 6 組設定:

| Config | TP % | 族群 | 排名出場 | 交易數 | 勝率 | 平均PnL% | PF |
|--------|------|------|---------|--------|------|----------|-----|
| A | 1.2/1.6/2.0 | Top 5 | No | 451 | 39.0% | -0.063% | 0.91 |
| B | 0.8/1.6/2.4 | Top 5 | No | 451 | 37.0% | -0.049% | 0.93 |
| C | 1.2/1.6/2.0 | Top 3 | No | 327 | 36.4% | -0.109% | 0.85 |
| D | 0.8/1.6/2.4 | Top 3 | No | 327 | 35.2% | -0.090% | 0.87 |
| E | 1.2/1.6/2.0 | Top 3 | Yes | 327 | 29.7% | -0.178% | 0.67 |
| F | 0.8/1.6/2.4 | Top 3 | Yes | 327 | 28.4% | -0.171% | 0.67 |

結論:
- Top 5 > Top 3 (更多交易、分散風險)
- TP 0.8/1.6/2.4 略優 (先鎖利)
- 排名出場大幅惡化 (52% 交易被提早出場)

---

## 新增參數組合測試步驟

1. 複製現有 YAML (如 `configs/mode_e_B.yaml`)
2. 修改目標參數
3. 在 `run_modeE_grid.ps1` 中新增 config entry
4. 執行腳本
5. 用 Python 讀取 `batch_trades.parquet` 分析結果:

```python
import pandas as pd
df = pd.read_parquet(r"D:\C#_backtest\modeE_grid\{name}\batch_trades.parquet")
completed = df[df['exit_price'].notna()]
print(f"Trades: {len(completed)}")
print(f"Win rate: {(completed.pnl_percent > 0).mean():.1%}")
print(f"Avg PnL: {completed.pnl_percent.mean():.3f}%")
print(f"Total PnL: {completed.pnl_amount.sum():,.0f}")
```

---

## 編譯與執行

```bash
# 編譯
cd D:\03_預估量相關資量\CSharp\StrongestVwap
dotnet build

# 單次執行
dotnet run -- --mode single --date 2026-01-14 --config configs/mode_e_group_screening.yaml \
  --screening_csv "..." --tick_data "D:\feature_data\feature"

# PowerShell 批次
powershell -ExecutionPolicy Bypass -File run_modeE_grid.ps1
```
