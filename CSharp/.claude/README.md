# .claude 專案配置

## Teams (Teammates)

啟用方式: `settings.json` 中設定 `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1`

### 可用 Teammates

| Teammate | 用途 | 適用場景 |
|----------|------|----------|
| `code-reviewer` | 程式碼審查 | PR review、新功能驗證、config 同步檢查、效能問題偵測 |
| `backtest-developer` | 核心開發 | 新增進出場策略、修復 bug、實作新功能、架構修改 |
| `performance-analyst` | 結果分析 | 比較策略績效、統計交易明細、參數敏感度分析 |
| `config-specialist` | 配置管理 | YAML 參數調整、參數網格設計、config 同步確認 |

### 使用範例

在 Claude Code 中用 `@code-reviewer` 或 `@backtest-developer` 來呼叫對應 teammate。

## Agent (舊版)

| Agent | 用途 |
|-------|------|
| `backtest-runner` | 執行回測 |
| `code-debugger` | 除錯 C# 回測程式 |
| `config-optimizer` | 參數優化 |
| `result-analyzer` | 分析回測結果 |

## Skill 知識庫

- `backtest-architecture/` — C# 回測系統架構知識
  - `core-architecture.md` — Core 層架構
  - `strategy-modules-architecture.md` — Strategy 層架構
  - `data-flow.md` — 資料流文件
  - `config-reference.md` — 設定參數參考
