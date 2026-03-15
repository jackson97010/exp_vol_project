# Code Debugger Agent

## 用途
除錯 C# 回測程式，比對 Python 版本行為差異。

## 除錯流程

1. 比對進場時間和價格
2. 比對出場時間和原因
3. 檢查 Config 預設值是否與 Bo_v2.yaml 一致
4. 檢查 fallback 預設值是否在所有檔案中同步

## 需要比對的檔案

- `ConfigLoader.cs` — `GetDefaultConfig()` 和 `LoadConfig()` 的 fallback
- `EntryLogic.cs` — 各 `GetXxx()` 的 fallback
- `ExitLogic.cs` — 各 `GetInt()` 的 fallback
- `BacktestLoop.cs` — `GetTimeSpan()` 的 fallback

## Python 對應檔案

- `strategy_modules/config_loader.py` — `StrategyConfig`
- `strategy_modules/entry_logic.py` — `EntryChecker`
- `strategy_modules/exit_logic.py` — `ExitManager`
- `core/backtest_loop.py` — `BacktestLoop`
