# C# Rewrite Design Document: BO Reentry Backtesting System

## Document Purpose

This document provides a complete analysis of the Python backtesting system (BO Reentry Strategy) so that Teammates 2 and 3 can rewrite everything in C# without referencing the Python source. Every class, method, data structure, and constant is documented here.

---

## Table of Contents

1. [Project Overview](#1-project-overview)
2. [Module-by-Module Analysis](#2-module-by-module-analysis)
3. [Data Flow](#3-data-flow)
4. [Python to C# Mapping Guide](#4-python-to-c-mapping-guide)
5. [C# Project Structure](#5-c-project-structure)
6. [Key Constants and Enums](#6-key-constants-and-enums)
7. [Interface Definitions](#7-interface-definitions)
8. [Configuration Schema](#8-configuration-schema)
9. [Critical Business Logic Details](#9-critical-business-logic-details)

---

## 1. Project Overview

### 1.1 What This System Does

This is a **Taiwan stock market intraday backtesting system** for a "Day High Breakout" (BO = Breakout) strategy. It:

1. Loads tick-level trade and order book (depth) data from Parquet files for a given stock and date.
2. Processes the data: merges trade and depth records, forward-fills order book columns.
3. Iterates through every tick, checking entry conditions (Day High breakout + multiple filters).
4. Manages positions with partial exits (trailing stops at 1/3 positions based on 1min/3min/5min lows), hard stop-losses, and market-close forced exits.
5. Generates trade statistics, CSV reports, and interactive Plotly charts (HTML + PNG).

### 1.2 Strategy Modes

- **Strategy A ("exhaustion")**: Momentum exhaustion exit -- exit when Day High growth rate slows down and order book weakens.
- **Strategy B ("simple trend")**: Trailing stop based on rolling N-minute lows, with tick-based hard stop-loss.

The YAML config `strategy_mode` selects which mode to use. Current default is "B".

### 1.3 Execution Modes

- **Single stock**: Run backtest for one stock on one date, produce chart + report.
- **Batch**: Run backtest for a list of stocks on one date, produce summary CSV.

### 1.4 Overall Architecture

```
bo_reentry.py (CLI entry point)
    |
    v
BacktestEngine (core/backtest_engine.py)
    |-- StrategyConfig (strategy_modules/config_loader.py)    -- loads YAML
    |-- DataProcessor (strategy_modules/data_processor.py)    -- loads/processes parquet data
    |-- EntryChecker (strategy_modules/entry_logic.py)        -- checks entry conditions
    |-- ExitManager (strategy_modules/exit_logic.py)          -- checks exit conditions
    |-- PositionManager (strategy_modules/position_manager.py)-- manages open positions
    |-- ReentryManager (strategy_modules/position_manager.py) -- manages re-entry logic
    |-- Indicators (strategy_modules/indicators.py)           -- technical indicator trackers
    |-- BacktestLoop (core/backtest_loop.py)                  -- main tick-by-tick loop
    |-- TradeStatisticsCalculator (analytics/)                -- compute stats
    |-- ReportGenerator (analytics/)                          -- console reports
    |-- TradeDetailsProcessor (analytics/)                    -- detailed trade records
    |-- CSVExporter (exporters/)                              -- CSV output
    |-- TradeExporter (exporters/)                            -- per-stock CSV output
    |-- ChartCreator (visualization/)                         -- Plotly charts
```

---

## 2. Module-by-Module Analysis

### 2.1 Main Entry: `bo_reentry.py`

**Purpose**: CLI entry point. Parses arguments, creates BacktestEngine, runs single or batch backtest.

**Key function**: `main()`
- Parses CLI arguments: `--mode` (single/batch), `--stock_id`, `--date`, `--stock_list`, `--use_screening`, `--no_csv`, `--no_chart`, `--config`, `--entry_start_time`, `--liquidity_multiplier`
- Creates `BacktestEngine(config_path)`
- Optionally overrides config parameters from CLI
- Calls `engine.run_single_backtest()` or `engine.run_batch_backtest()`

**Key function**: `load_screening_results(date: str) -> List[str]`
- Loads a CSV file of screening results
- Returns stock ID list for the given date

**C# equivalent**: A console application `Program.cs` with `CommandLineParser` or manual argument parsing.

---

### 2.2 Configuration: `Bo_v2.yaml`

**Purpose**: YAML config file with all strategy parameters. Has three top-level sections:

#### Section `strategy:` (most important)
| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| strategy_mode | string | "B" | "A" = exhaustion, "B" = simple trend |
| entry_start_time | string (HH:MM:SS) | "09:05:00" | Earliest time to allow entry |
| entry_cutoff_time | string (HH:MM:SS) | "13:00:00" | Latest time to allow entry |
| entry_cooldown | float | 30.0 | Seconds after exit before next entry |
| above_open_check_enabled | bool | false | Require price > previous close |
| ask_bid_ratio_threshold | float | 1.0 | Min ask/bid ratio for entry |
| massive_matching_enabled | bool | true | Enable large matching amount check |
| massive_matching_amount | float | 50,000,000 | Fixed threshold (when dynamic disabled) |
| use_dynamic_liquidity_threshold | bool | false | Use dynamic threshold from parquet |
| dynamic_liquidity_multiplier | float | 0.004 | Multiplier for dynamic threshold |
| dynamic_liquidity_threshold_cap | float | 50,000,000 | Cap for dynamic threshold |
| ratio_entry_enabled | bool | true | Enable ratio check |
| ratio_entry_threshold | float | 3 | Min ratio for entry |
| ratio_column | string | "ratio_15s_300s" | Column name for ratio data |
| interval_pct_filter_enabled | bool | true | Enable interval % filter |
| interval_pct_minutes | int | 5 | Minutes window for interval check |
| interval_pct_threshold | float | 3.0 | Max allowed interval % |
| breakout_quality_check_enabled | bool | false | Enable breakout quality check |
| breakout_min_volume | int | 10 | Min volume for breakout |
| breakout_min_ask_eat_ratio | float | 0.25 | Min ask eat ratio |
| breakout_absolute_large_volume | int | 50 | Absolute large order threshold |
| small_order_filter_enabled | bool | false | Enable small order filter |
| entry_buffer_enabled | bool | true | Enable entry buffer (wait after breakout) |
| entry_buffer_milliseconds | int | 100 | Buffer wait time in ms |
| reentry | bool | false | Enable re-entry logic |
| grace_period | float | 3.0 | Grace period after entry (no stop-loss) |
| price_stop_loss_ticks | int | 3 | Hard stop ticks below Day High |
| strategy_b_stop_loss_ticks_small | int | 3 | Stop ticks for small tick sizes |
| strategy_b_stop_loss_ticks_large | int | 2 | Stop ticks for large tick sizes |
| strategy_b_trailing_stop_minutes | int | 3 | Trailing stop window (minutes) |
| trailing_stop.enabled | bool | true | Enable multi-level trailing stop |
| trailing_stop.levels | list | see below | Trailing stop level configs |
| trailing_stop.entry_price_protection | bool | true | Exit all if price falls below entry |
| ratio_increase_after_loss_enabled | bool | true | Raise ratio threshold after stop-loss |
| stop_loss_limit_enabled | bool | false | Limit number of stop-losses |

**Trailing stop levels** (list of objects):
```yaml
levels:
  - name: '1min'
    field: 'low_1m'
    exit_ratio: 0.333
  - name: '3min'
    field: 'low_3m'
    exit_ratio: 0.333
  - name: '5min'
    field: 'low_5m'
    exit_ratio: 0.334
```

#### Section `close_prices:`
| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| file | string | "close.parquet" | Path to close price parquet file |

#### Section `output:`
| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| record_ticks | bool | true | Record all tick data |
| output_dir | string | "logs" | Log output directory |
| generate_html | bool | true | Generate HTML charts |
| generate_png | bool | true | Generate PNG images |
| png_output_dir | string | "D:\\backtest_results" | PNG output directory |
| save_backtest_data | bool | true | Save backtest data as parquet |

---

### 2.3 Core Module: `core/constants.py`

**Purpose**: Centralized constants.

```
OUTPUT_BASE_DIR = "D:\\backtest_results"
SCREENING_RESULTS_PATH = "C:\\...\\screening_results.csv"
TOTAL_TRADES_OUTPUT = OUTPUT_BASE_DIR + "\\backtest_trades_total.csv"
DEFAULT_CONFIG_PATH = "Bo_v2.yaml"

DEFAULT_SHARES_PER_TRADE = 1000       // 1 lot = 1000 shares
MAX_GAIN_PERCENTAGE = 8.5
MARKET_OPEN_TIME_LIMIT = "09:05:00"
MARKET_CLOSE_TIME = "13:30:00"

DAY_HIGH_MOMENTUM_WINDOW = 60        // seconds
OUTSIDE_VOLUME_WINDOW = 3            // seconds
MASSIVE_MATCHING_WINDOW = 1          // seconds
IO_RATIO_WINDOW = 60                 // seconds
LARGE_ORDER_THRESHOLD = 10           // lots

ORDER_BOOK_THIN_THRESHOLD = 20
ORDER_BOOK_NORMAL_THRESHOLD = 40

BUFFER_DURATION_SECONDS = 3

LOG_FORMAT = "%(asctime)s - %(levelname)s - %(message)s"
LOG_DATE_FORMAT = "%H:%M:%S"
```

---

### 2.4 Core Module: `core/backtest_engine.py`

#### Class: `BacktestEngine`

**Purpose**: Orchestrates all modules. Creates and initializes all sub-modules, then delegates to BacktestLoop.

**Constructor**: `__init__(self, config_path: str = DEFAULT_CONFIG_PATH)`
- Creates `StrategyConfig` from YAML path
- Extracts entry_config, exit_config, reentry_config dicts
- Calls `_init_core_modules()`, `_init_analytics_modules()`, `_init_indicators()`

**Fields**:
- `config`: StrategyConfig
- `entry_config`: Dict -- entry conditions
- `exit_config`: Dict -- exit conditions
- `reentry_config`: Dict -- reentry conditions
- `data_processor`: DataProcessor
- `entry_checker`: EntryChecker
- `exit_manager`: ExitManager
- `position_manager`: PositionManager
- `reentry_manager`: ReentryManager
- `chart_creator`: ChartCreator
- `statistics_calculator`: TradeStatisticsCalculator
- `report_generator`: ReportGenerator
- `details_processor`: TradeDetailsProcessor
- `csv_exporter`: CSVExporter
- `trade_exporter`: TradeExporter
- `momentum_tracker`: DayHighMomentumTracker
- `orderbook_monitor`: OrderBookBalanceMonitor
- `outside_volume_tracker`: OutsideVolumeTracker (3s window)
- `massive_matching_tracker`: MassiveMatchingTracker (1s window)
- `inside_outside_ratio_tracker`: InsideOutsideRatioTracker (60s window)
- `large_order_io_ratio_tracker`: InsideOutsideRatioTracker (60s window, min_volume=10)
- `entry_signal_log`: List[Dict]
- `last_tick_data`: DataFrame (nullable)

**Methods**:

| Method | Parameters | Returns | Description |
|--------|-----------|---------|-------------|
| `run_single_backtest` | stock_id: str, date: str, silent: bool=False | List[TradeRecord] | Run backtest for one stock. Resets position manager, loads data, gets price info, runs BacktestLoop, generates outputs. |
| `run_batch_backtest` | stock_list: List[str], date: str, output_csv: bool=True, create_charts: bool=True | List[Dict] | Iterates stock_list, calls run_single_backtest for each, collects stats, exports CSV. |
| `_load_and_process_data` | stock_id: str, date: str | DataFrame | Loads parquet feature data and processes it. |
| `_get_price_info` | df: DataFrame, stock_id: str, date: str | Tuple[float, float, float] | Returns (ref_price, limit_up_price, limit_down_price). |
| `_generate_outputs` | df, trades, stock_id, date, ref_price, limit_up_price | None | Generates reports, CSV, and charts. |
| `_create_visualization` | df, trades, stock_id, date, ref_price, limit_up_price | None | Converts TradeRecord objects to dict format and calls chart_creator. |

---

### 2.5 Core Module: `core/backtest_loop.py`

#### Class: `BacktestLoop`

**Purpose**: The main tick-by-tick backtesting loop. This is the heart of the system.

**Constructor**: `__init__(self, engine: BacktestEngine)`
- Stores references to all engine sub-modules (entry_checker, exit_manager, position_manager, etc.)
- `last_exit_was_stop_loss`: bool -- tracks if last exit was a stop-loss (for ratio threshold escalation)

**Primary method**: `run(df: DataFrame, stock_id: str, ref_price: float, limit_up_price: float) -> List[TradeRecord]`

Algorithm:
1. Initialize loop state dict and metrics lists
2. Convert DataFrame columns to a list for fast iteration
3. For each row (converted to dict via `zip(columns, row_tuple)`):
   a. Update current state (time, price, day_high, tick_type, volume, bid_ask_ratio)
   b. Update all indicator trackers
   c. Calculate and record metrics
   d. If has position: run exit logic
   e. If no position and price > 0: run entry logic
   f. Update prev_day_high (only for price > 0 rows)
4. Force close at market close
5. Attach metric columns to DataFrame
6. Return trade history

**Loop State Dict** (initialized by `_init_loop_state`):
```python
{
    'prev_day_high': 0,
    'last_entry_time': None,
    'last_exit_time': None,
    'day_high_break_count': 0,
    'day_high_break_count_after_entry_time': 0,
    'last_bid_ask_ratio': None,
    'last_entry_ratio': 0.0,
    'dynamic_ratio_threshold': None,
    'fixed_ratio_threshold': None,
    'first_entry_outside_volume': None,
    'current_time': None,          // DateTime
    'current_price': 0,            // float
    'current_day_high': 0,         // float
    'current_tick_type': 0,        // int (1=outside/buy, 2=inside/sell)
    'current_volume': 0,           // float (lots)
    'breakout_buffer_state': {
        'active': False,
        'start_time': None,
        'day_high': 0.0,
        'checked': False
    },
    'waiting_for_outside_entry': {
        'active': False,
        'breakout_time': None,
        'breakout_day_high': 0.0,
        'prev_day_high': 0.0
    },
    'reentry_buffer_state': {
        'active': False,
        'start_time': None,
        'day_high': 0.0,
        'checked': False,
        'max_outside_volume': 0.0
    }
}
```

**Metrics Lists Dict** (initialized by `_init_metrics_lists`):
```python
{
    'day_high_growth_rates': [],
    'bid_avg_volumes': [],
    'ask_avg_volumes': [],
    'balance_ratios': [],
    'day_high_breakouts': [],
    'inside_outside_ratios': [],
    'outside_ratios': [],
    'large_order_io_ratios': [],
    'large_order_outside_ratios': []
}
```

**Key methods**:

| Method | Description |
|--------|-------------|
| `_update_current_state(row, state)` | Extracts current time, price, day_high, tick_type, volume, bid_ask_ratio from row into state dict. |
| `_update_indicators(row, state)` | Updates all 5 indicator trackers with current tick data. |
| `_calculate_metrics(row, state, metrics)` | Calculates growth rate, bid/ask thickness, balance ratio, IO ratios, breakout detection. Appends to metrics lists. |
| `_process_exit_logic(state, row, limit_up_price)` | Checks limit-up exit, trailing stop, momentum exhaustion, hard stop-loss, and reentry in priority order. |
| `_process_entry_logic(row, stock_id, ref_price, limit_up_price, state, df)` | Handles "waiting for outside tick" state machine. On breakout: sets waiting state. On outside tick after breakout: proceeds to check all entry conditions. |
| `_check_entry(row, stock_id, ref_price, ...)` | Checks buffer timing, entry start/cutoff times, builds indicators dict, resolves bid_ask_ratio, calls entry_checker.check_entry_signals(). |
| `_execute_entry(entry_signal, state, row)` | Opens position via position_manager, records entry info in state. |
| `_handle_exit(exit_result, current_time, state)` | Routes to partial_exit or close_position based on exit_result['exit_type']. Tracks last_exit_was_stop_loss. |
| `_handle_trailing_exit(exit_result)` | Calls position_manager.trailing_stop_exit(). If remaining_ratio <= 0, calls _close_position_completely(). |
| `_close_position_completely(position, trailing_result, state)` | Calculates weighted average exit price from all trailing exits, closes position. |
| `_force_close_at_market_close(state)` | If position still open at end of data, closes it with reason "market close". |
| `_add_metrics_to_dataframe(df, metrics)` | Attaches all computed metrics as new columns to the DataFrame. |

**CRITICAL ENTRY LOGIC -- "Waiting for Outside Tick" State Machine**:

When a Day High breakout is detected:
1. The system does NOT enter immediately (even if the breakout tick is an outside tick).
2. It enters a "waiting" state, recording the breakout time and day_high.
3. On subsequent ticks, if a tick is: (a) outside tick (tick_type == 1) AND (b) has a different timestamp than the breakout, THEN proceed to check all entry conditions.
4. This prevents entering on the same tick that created the breakout (realistic: you observe the breakout, THEN react).

---

### 2.6 Strategy Module: `strategy_modules/config_loader.py`

#### Class: `StrategyConfig`

**Purpose**: Loads YAML config, parses values, provides typed access.

**Constructor**: `__init__(self, config_path: str = "Bo_v2.yaml")`
- Loads YAML file
- Merges with defaults
- Parses time strings to `time` objects
- Loads dynamic liquidity threshold parquet if enabled

**Key Methods**:
| Method | Returns | Description |
|--------|---------|-------------|
| `get_entry_config()` | Dict | Returns all entry-related settings |
| `get_exit_config()` | Dict | Returns all exit-related settings (including trailing_stop nested dict) |
| `get_reentry_config()` | Dict | Returns reentry-related settings |
| `get_all_config()` | Dict | Returns full config copy |

**Private methods**:
- `_load_config() -> Dict`: Reads YAML, merges with defaults, parses all values
- `_get_default_config() -> Dict`: Returns default values dictionary
- `_parse_time(value: str, fallback: time) -> time`: Parses "HH:MM:SS" string

---

### 2.7 Strategy Module: `strategy_modules/data_processor.py`

#### Class: `DataProcessor`

**Purpose**: Loads Parquet feature data, processes trade/depth data, calculates tick sizes and limit prices.

**Constructor**: `__init__(self, config: Dict)`
- `config`: full config dict
- `company_info_cache`: None initially, loaded on first call to get_company_name

**Methods**:

| Method | Parameters | Returns | Description |
|--------|-----------|---------|-------------|
| `load_feature_data` | stock_id: str, date: str | DataFrame | Loads `D:/feature_data/feature/{date}/{stock_id}.parquet`, converts time column, filters 09:00-13:30. |
| `process_trade_data` | df: DataFrame | DataFrame | Separates Trade/Depth rows, replaces 0 with NaN in order book columns, merges and forward-fills, keeps only Trade rows. Also ensures bid_volume_5level and ask_volume_5level are computed. |
| `merge_depth_data` | trade_df, depth_df | DataFrame | Alternative merge method (same logic as process_trade_data). |
| `get_reference_price` | stock_id: str, date: str, close_path: str=None | float or None | Reads close.parquet, finds previous trading day's close price for this stock. |
| `get_tick_size_decimal` | price: float | Decimal | **STATIC**. Returns tick size based on Taiwan Stock Exchange rules. |
| `calculate_limit_up` | previous_close: float | float | **STATIC**. Computes limit-up price: (close * 1.10) rounded DOWN to tick. |
| `calculate_limit_down` | previous_close: float | float | **STATIC**. Computes limit-down price: (close * 0.90) rounded UP to tick. |
| `add_calculated_columns` | df: DataFrame | DataFrame | Adds placeholder columns (day_high_growth_rate, bid_avg_volume, etc.). |
| `get_company_name` | stock_id: str | str | Looks up company name from company_basic_info.parquet. |
| `filter_trading_hours` | df: DataFrame | DataFrame | Filters to 09:00-13:30. |
| `validate_data` | df: DataFrame | bool | Checks required columns exist, data non-empty, time sorted. |

**Taiwan Stock Exchange Tick Size Table** (CRITICAL for C# implementation):
| Price Range | Tick Size |
|------------|-----------|
| >= 1000 | 5 |
| >= 500 | 1 |
| >= 100 | 0.5 |
| >= 50 | 0.1 |
| >= 10 | 0.05 |
| < 10 | 0.01 |

**Order Book Columns** (forward-filled from Depth to Trade rows):
```
bid1_volume, bid2_volume, bid3_volume, bid4_volume, bid5_volume
ask1_volume, ask2_volume, ask3_volume, ask4_volume, ask5_volume
bid_volume_5level, ask_volume_5level, bid_ask_ratio
```

---

### 2.8 Strategy Module: `strategy_modules/entry_logic.py`

#### Dataclass: `EntrySignal`

**Fields**:
| Field | Type | Description |
|-------|------|-------------|
| time | DateTime | Signal timestamp |
| stock_id | str | Stock code |
| price | float | Current price |
| day_high | float | Current Day High |
| passed | bool | Whether all conditions passed |
| conditions | Dict[str, Dict] | Results for each condition check |
| failure_reason | str | Reason for failure (if any) |

**Method**: `to_dict() -> Dict` -- serializes to dictionary.

#### Class: `EntryChecker`

**Purpose**: Checks all entry conditions in order. Each condition is checked sequentially; if any fails, the signal is rejected early (short-circuit).

**Constructor**: `__init__(self, config: Dict)`
- `config`: entry config dict
- `entry_signals`: List[EntrySignal] -- records all signals for statistics
- `small_order_filter`: SmallOrderFilter instance

**Method**: `check_day_high_breakout(current_day_high: float, prev_day_high: float) -> bool`
- Returns `current_day_high > prev_day_high and prev_day_high > 0`

**Method**: `check_entry_signals(...)` -- THE MAIN ENTRY CHECK

Parameters:
- stock_id, current_price, current_time, prev_day_high
- indicators: Dict with keys: ratio, pct_2min, pct_3min, pct_5min, low_1m, low_3m, low_5m, low_3min, low_10min, low_15min
- ask_bid_ratio: float
- ref_price: float
- massive_matching_amount: float
- fixed_ratio_threshold, dynamic_ratio_threshold: Optional[float]
- min_outside_amount: Optional[float] (for reentry)
- force_log: bool
- df: Optional DataFrame (for small order filter)
- current_row: Optional (for breakout quality check)
- is_day_high_breakout: bool
- last_exit_time: Optional DateTime

Returns: `EntrySignal`

**Entry Conditions Checked (in order)**:

1. **Time check**: current_time >= entry_start_time AND < entry_cutoff_time
2. **Cooldown check**: time since last_exit_time >= entry_cooldown
3. **Above-open check** (optional): current_price > ref_price
4. **Price change limit**: (current_price - ref_price) / ref_price * 100 <= 8.5%
5. **Order book ratio**: ask_bid_ratio >= ask_bid_ratio_threshold (e.g., 1.0)
6. **Massive matching**: 1-second outside volume amount >= threshold (dynamic or fixed)
7. **Outside amount comparison** (reentry only): amount > min_outside_amount
8. **Ratio condition**: ratio > threshold (supports OR logic with fixed/dynamic thresholds)
9. **Interval % filter**: pct_Nmin <= interval_pct_threshold
10. **Low point filter**: 3min/10min/15min low point check
11. **Non-limit-up check**: price change < 10%
12. **Breakout quality / small order filter**: checks breakout volume quality

**Method**: `get_entry_signals_summary() -> DataFrame` -- statistics of all signals.

---

### 2.9 Strategy Module: `strategy_modules/exit_logic.py`

#### Class: `ExitManager`

**Purpose**: Manages all exit conditions.

**Constructor**: `__init__(self, config: Dict)`
- Reads trailing_stop config (enabled, levels, entry_price_protection)
- `trailing_stop_enabled`: bool
- `trailing_stop_levels`: List[Dict] with name, field, exit_ratio
- `entry_price_protection`: bool

**Tick Size helper methods** (same table as DataProcessor):
- `_get_tick_size(price: float) -> float`
- `_add_ticks(price: float, ticks: int) -> float`: price + (tick_size * ticks)

**Methods**:

| Method | Description |
|--------|-------------|
| `check_hard_stop_loss(position, current_price, current_time) -> Optional[Dict]` | Checks if price <= day_high_at_entry - N ticks. Uses different tick counts for small vs large tick sizes. Returns exit dict with exit_type="remaining", exit_reason="tick_stop_loss". |
| `check_limit_up_exit(position, current_price, current_time, limit_up_price) -> Optional[Dict]` | If price >= limit_up_price, returns partial exit (50%). |
| `check_momentum_exhaustion(position, row, momentum_tracker, orderbook_monitor, current_time, current_price) -> Optional[Dict]` | Strategy A only. Requires 60s holding. Checks: (growth_rate < 0.86%) OR (growth_drawdown >= 0.5% AND buy_weak_streak >= 5). Returns partial exit (50%). |
| `check_partial_exit(position, row, momentum_tracker, orderbook_monitor, limit_up_price) -> Optional[Dict]` | Combines hard stop + limit up + momentum exhaustion for first stage exit. |
| `check_final_exit(position, row, current_time, current_price) -> Optional[Dict]` | Second stage: after partial exit done, checks if price <= max(low_3m, entry_price). Exits remaining position. |
| `check_reentry_stop_loss(position, row, momentum_tracker, orderbook_monitor) -> Optional[Dict]` | For reentry positions: checks price stop, momentum exhaustion (after 30s), and price below reentry price. |
| `check_trailing_stop(position, row, current_time, current_price) -> Optional[Dict]` | Checks each trailing stop level (1min, 3min, 5min low). If price <= low, triggers that level. Returns exit with exit_ratio, exit_level. |
| `check_entry_price_protection(position, current_price, current_time) -> Optional[Dict]` | After partial exit, if price <= entry_price, exit all remaining. |
| `check_new_exit_logic(position, row, momentum_tracker, orderbook_monitor, limit_up_price) -> Optional[Dict]` | Integrated exit logic checking all conditions. |

**Exit Result Dict Structure**:
```csharp
{
    "exit_type": "partial" | "remaining" | "trailing_stop" | "protection" | "reentry_stop",
    "exit_ratio": float,     // 0.0-1.0
    "exit_reason": string,
    "exit_price": float,
    "exit_time": DateTime,
    "exit_level": string,    // for trailing stop: "1min", "3min", "5min"
    // ... additional diagnostic fields
}
```

---

### 2.10 Strategy Module: `strategy_modules/position_manager.py`

#### Class: `Position`

**Purpose**: Represents an open position with all tracking state.

**Constructor Parameters**:
- entry_time: DateTime
- entry_price: float
- entry_bid_thickness: float
- day_high_at_entry: float
- entry_ratio: float
- entry_outside_volume_3s: float

**Fields**:
| Field | Type | Default | Description |
|-------|------|---------|-------------|
| entry_time | DateTime | - | Entry timestamp |
| entry_price | float | - | Entry price |
| entry_bid_thickness | float | - | Bid thickness at entry |
| day_high_at_entry | float | - | Day High when entered |
| entry_ratio | float | - | Ratio at entry |
| entry_outside_volume_3s | float | 0.0 | 3-second outside volume at entry |
| highest_price | float | entry_price | Highest price since entry |
| partial_exit_done | bool | False | Whether first partial exit occurred |
| partial_exit_time | DateTime? | None | Time of partial exit |
| partial_exit_price | float? | None | Price of partial exit |
| partial_exit_recovered | bool | False | Whether reentry occurred |
| reentry_time | DateTime? | None | Reentry timestamp |
| reentry_price | float? | None | Reentry price |
| reentry_stop_price | float? | None | Stop-loss price for reentry |
| final_exit_time | DateTime? | None | Final exit timestamp |
| final_exit_price | float? | None | Final exit price |
| buy_weak_streak | int | 0 | Consecutive weak buy ticks |
| share_capital | float | 0 | Share capital (for momentum check) |
| trailing_exits | List[Dict] | [] | Records of each trailing stop exit |
| remaining_ratio | float | 1.0 | Remaining position ratio |
| exit_levels_triggered | Dict[str,bool] | {"1min":false,"3min":false,"5min":false} | Which trailing levels fired |
| allow_reentry | bool | - | Set from config on entry |

#### Dataclass: `TradeRecord`

**Purpose**: Completed trade record (from entry to full exit).

**Fields**:
| Field | Type | Default |
|-------|------|---------|
| entry_time | DateTime | required |
| entry_price | float | required |
| entry_bid_thickness | float | required |
| entry_ratio | float | required |
| day_high_at_entry | float | required |
| entry_outside_volume_3s | float | 0.0 |
| partial_exit_time | DateTime? | None |
| partial_exit_price | float? | None |
| partial_exit_reason | str | "" |
| reentry_time | DateTime? | None |
| reentry_price | float? | None |
| reentry_stop_price | float? | None |
| reentry_outside_volume_3s | float | 0.0 |
| final_exit_time | DateTime? | None |
| final_exit_price | float? | None |
| final_exit_reason | str | "" |
| reentry_exit_reason | str? | None |
| pnl_percent | float? | None |
| trailing_exit_details | List[Dict] | [] |
| total_exits | int | 0 |
| final_remaining_ratio | float | 1.0 |

**Method**: `to_dict() -> Dict`

#### Class: `PositionManager`

**Purpose**: Manages the current open position and trade history.

**Fields**:
- `current_position`: Position? (nullable)
- `current_trade_record`: TradeRecord? (nullable)
- `trade_history`: List[TradeRecord]

**Methods**:

| Method | Parameters | Returns | Description |
|--------|-----------|---------|-------------|
| `reset()` | - | void | Clears all state. |
| `open_position` | entry_time, entry_price, entry_bid_thickness, day_high_at_entry, entry_ratio, entry_outside_volume_3s | Position | Creates new Position and TradeRecord. |
| `partial_exit` | exit_time, exit_price, exit_reason | bool | Sets partial_exit_done, remaining_ratio=0.5, sets reentry_stop_price. |
| `reentry_position` | reentry_time, reentry_price, reentry_outside_volume_3s | bool | Sets partial_exit_recovered, updates reentry fields. |
| `trailing_stop_exit` | exit_time, exit_price, exit_ratio, exit_level, exit_reason | bool | Decrements remaining_ratio, records trailing exit detail, marks level as triggered. |
| `close_position` | exit_time, exit_price, exit_reason, is_reentry_exit=False | TradeRecord? | Calculates PnL, appends to trade_history, resets current position. |
| `has_position()` | - | bool | Returns current_position != null AND remaining_ratio > 0. |
| `get_current_position()` | - | Position? | Returns current position. |
| `get_trade_history()` | - | List[TradeRecord] | Returns trade history. |

**PnL Calculation** (in `close_position`):
- If trailing exits exist: sum of (exit_price - entry_price) / entry_price * 100 * exit_ratio for each exit, plus remaining.
- If partial exit exists: 50% at partial price + 50% at final price.
- Otherwise: 100% at final price.

#### Class: `ReentryManager`

**Purpose**: Checks conditions for re-entering a partially exited position.

**Constructor**: `__init__(self, config: Dict)`

**Methods**:
| Method | Description |
|--------|-------------|
| `check_reentry_conditions(current_row, position, outside_volume_tracker) -> Dict` | Checks: (1) partial exit done, (2) not already recovered, (3) price > highest_price, (4) current 3s outside volume > entry 3s outside volume. |
| `check_reentry_conditions_with_volume(current_row, position, override_volume) -> Dict` | Same but uses specified volume instead of tracker. |

---

### 2.11 Strategy Module: `strategy_modules/indicators.py`

#### Class: `DayHighMomentumTracker`

**Purpose**: Tracks Day High growth rate over a sliding window.

**Constructor**: `__init__(self, window_seconds: int = 60)`
- `day_high_history`: deque of {time, day_high}
- `growth_rate_history`: deque(maxlen=20)
- `peak_growth_rate`: float

**Methods**:
| Method | Description |
|--------|-------------|
| `update(current_time, day_high)` | Adds record, removes expired (outside window), computes 1-min-ago day_high. |
| `get_growth_rate() -> float` | (current_day_high - day_high_1min_ago) / day_high_1min_ago. Updates peak. |
| `is_growth_rate_turning_down() -> bool` | Current rate < previous rate. |
| `get_growth_drawdown() -> float` | peak_growth_rate - last_growth_rate. |

#### Class: `OrderBookBalanceMonitor`

**Purpose**: Analyzes order book (bid/ask) thickness and balance.

**Constructor**: `__init__(self, thin_threshold: int = 20, normal_threshold: int = 40)`

**Methods**:
| Method | Description |
|--------|-------------|
| `calculate_bid_thickness(row) -> float` | Sum of bid1-bid5 volumes / 5. Falls back to bid_volume_5level / 5. |
| `calculate_ask_thickness(row) -> float` | Same for ask side. |
| `calculate_balance_ratio(row) -> float` | total_bid / total_ask. Returns 0 if either is 0. |
| `is_order_book_recovering(current_row, entry_bid_thickness) -> bool` | Was thin at entry, now normal. |

#### Class: `OutsideVolumeTracker`

**Purpose**: Tracks outside (buy) volume amount in a sliding time window.

**Constructor**: `__init__(self, window_seconds: int = 3)`
- `trades_window`: deque of {time, amount}
- `total_volume`: float (running sum)

**Methods**:
| Method | Description |
|--------|-------------|
| `update_trades(current_time, tick_type, price, volume)` | Removes expired trades from window. If tick_type == 1 (outside/buy), adds price * volume * 1000 to window. |
| `get_volume_3s() -> float` | Returns total_volume (same as get_current_volume). |
| `get_current_volume() -> float` | Returns total_volume. |
| `compare_with_entry(entry_volume) -> Tuple[float, bool]` | Returns (current, current > entry_volume). |
| `reset()` | Clears window and total. |
| `get_trade_count() -> int` | Number of trades in window. |
| `get_average_trade_amount() -> float` | total_volume / count. |

**IMPORTANT**: Amount = price * volume * 1000 (volume is in lots, 1 lot = 1000 shares).

#### Class: `MassiveMatchingTracker`

**Purpose**: Wraps OutsideVolumeTracker with 1-second window for "massive matching" detection.

**Constructor**: `__init__(self, window_seconds: int = 1)`
- Contains an `OutsideVolumeTracker(window_seconds=1)`

**Methods**: `update(...)`, `get_massive_matching_amount() -> float`, `check_threshold(threshold) -> bool`

#### Class: `InsideOutsideRatioTracker`

**Purpose**: Tracks inside/outside (sell/buy) volume ratio over a sliding window.

**Constructor**: `__init__(self, window_seconds: int = 60, min_volume_threshold: float = 0)`
- `inside_trades`, `outside_trades`: deques
- `inside_volume`, `outside_volume`: float running sums
- Trades with volume <= min_volume_threshold are ignored

**Methods**:
| Method | Description |
|--------|-------------|
| `update(current_time, tick_type, price, volume)` | If volume > threshold: add to inside (type=2) or outside (type=1) deque. Clean expired. |
| `get_ratio() -> float` | inside_volume / outside_volume (0 if outside is 0). |
| `get_outside_ratio() -> float` | outside_volume / (inside + outside) (0.5 if total is 0). |
| `get_volumes() -> Tuple[float, float]` | (inside_volume, outside_volume). |
| `reset()` | Clears all. |

---

### 2.12 Strategy Module: `strategy_modules/small_order_filter.py`

#### Class: `SmallOrderFilter`

**Purpose**: Filters out "small order push" fake breakouts and checks breakout quality.

**Constructor**: `__init__(self, config: Dict)`
- `enabled`: bool (small order filter)
- `breakout_quality_enabled`: bool
- Various thresholds for small/tiny/single/large orders and ratios

**Methods**:

| Method | Description |
|--------|-------------|
| `check_small_order_pattern(df, current_time) -> Tuple[bool, str]` | Checks last N trades: if single/tiny/small order ratios exceed limits, reject. |
| `check_breakout_quality(current_row, is_day_high_breakout, df) -> Tuple[bool, str]` | Checks if breakout tick has sufficient volume. Absolute large (>=50 lots) passes. Otherwise needs >= min_volume AND eat_ratio >= 25% of ask 5-level. |
| `get_trade_statistics(df, current_time) -> Dict` | Returns trade count/volume statistics. |
| `analyze_breakout_context(df, breakout_time, seconds_before, seconds_after) -> Dict` | Compares volume before/after breakout. |

---

### 2.13 Analytics Module: `analytics/trade_statistics.py`

#### Class: `TradeStatisticsCalculator`

**Constructor**: `__init__(self, data_processor=None)` -- uses data_processor for company names.

**Method**: `calculate_statistics(stock_id, trades: List[TradeRecord], date) -> Dict`
- Returns dict with keys: stock_id, stock_name, entry_count, stop_loss_count, win_rate%, entry_price, exit_price, pnl

**Method**: `_calculate_exit_price(trade) -> float`
- Weighted average of trailing exits, or 50/50 split for two-stage, or single exit price.

---

### 2.14 Analytics Module: `analytics/report_generator.py`

#### Class: `ReportGenerator`

**Constructor**: `__init__(self, entry_checker=None)`

**Methods**:
| Method | Description |
|--------|-------------|
| `generate_report(df, trades, stock_id, date)` | Logs trade details and entry signal statistics. |
| `generate_summary_report(results: List[Dict], date) -> DataFrame` | Creates summary DataFrame from batch results, logs totals. |

---

### 2.15 Analytics Module: `analytics/trade_details.py`

#### Class: `TradeDetailsProcessor`

**Purpose**: Converts TradeRecord objects into flat detail dictionaries for CSV export.

**Methods**:
| Method | Description |
|--------|-------------|
| `collect_trade_details(trades, stock_id, date) -> List[Dict]` | For each trade: calculates actual exit price, then calls _add_trailing_exit_details or _add_two_stage_exit_details. |
| `calculate_actual_exit_price(trade) -> float` | Weighted average of all exit prices. |

**Detail Dict keys** (Chinese column names for CSV):
```
stock_code, trade_number, entry_time, entry_price, entry_ratio,
entry_outside_3s_M, exit_batch, exit_type, exit_time, exit_price,
exit_ratio, exit_reason, actual_avg_exit_price, total_pnl_amount,
total_pnl_percent
```

---

### 2.16 Exporters Module: `exporters/csv_exporter.py`

#### Class: `CSVExporter`

**Constructor**: `__init__(self, output_base_dir: str = "D:\\backtest_results")`

**Methods**:
| Method | Description |
|--------|-------------|
| `export_summary_to_csv(results: List[Dict], date)` | Writes summary stats to `{base}/{date}/backtest_summary_{date}.csv`. |
| `export_detailed_trades_to_csv(all_trade_details: List[Dict], date)` | Writes detailed trades to CSV, sorted by stock + entry time. Also calls _export_batch_script_csv. |
| `_export_batch_script_csv(all_trade_details, date)` | Writes `backtest_results_{date}.csv` with trade_id, entry_time, stock_id, entry_price, exit_price, exit_ratio, exit_type. |

---

### 2.17 Exporters Module: `exporters/trade_exporter.py`

#### Class: `TradeExporter`

**Constructor**: `__init__(self, output_base_dir: str = "D:\\backtest_results")`
- Contains a `TradeDetailsProcessor`

**Methods**:
| Method | Description |
|--------|-------------|
| `export_trade_details_to_csv(trades, stock_id, date)` | Writes per-stock CSV: `{base}/{date}/{stock_id}_trade_details_{date}.csv`. |

---

### 2.18 Exporters Module: `exporters/early_entry_exporter.py`

#### Class: `EarlyEntryExporter`

**Purpose**: Specialized exporter for early-morning strategy trades.

**Constructor**: `__init__(self, base_path: str = "D:/1min_entry", config=None)`

**Methods**: `set_date(date)`, `add_trade(trade_record)`, `export_single_trade(stock_id, trade_record, df, massive_threshold)`, `export_summary()`, `export_performance_report()`

**Profit calculation**: Uses commission = position_value * 0.0017 (Taiwan stock brokerage fee).

---

### 2.19 Exporters Module: `exporters/early_chart_creator.py`

#### Class: `EarlyChartCreator`

**Purpose**: Creates simplified charts for early-morning strategy.

**Constructor**: `__init__(self, config=None)`

**Method**: `create_chart(df, trade_record, output_path, stock_id, date, massive_threshold) -> str`
- 3-row subplot: Price+DayHigh, Massive matching amount, Order book ratio

---

### 2.20 Visualization Module: `visualization/chart_creator.py`

#### Class: `ChartCreator`

**Purpose**: Creates comprehensive strategy charts with 5 subplots.

**Constructor**: `__init__(self, config=None)`

**Method**: `create_strategy_chart(df, trades: List[Dict], output_path, png_output_path=None, ref_price=None, limit_up_price=None, stock_id=None, company_name=None) -> str`

**Subplots** (5 rows):
1. **Price chart**: Price line, Day High line, 1/3/5 min low lines, entry/exit markers, ref price line, limit-up line
2. **Ratio chart**: ratio_15s_300s line, entry threshold line, "entry allowed" shaded area
3. **Growth rate chart**: Day High growth rate %, 0.86% threshold, "exhaustion" shaded area
4. **Orderbook thickness**: Bid/ask average volumes, thin/normal threshold lines
5. **Balance ratio**: Buy/sell balance ratio, 1.0 reference line, balanced zone

**Trade markers**:
- Entry: red circle (size 15)
- Trailing stop exits: colored by level (green=1min, orange=3min, purple=5min), triangle-down
- Partial exit (50%): orange triangle-down
- Reentry: blue triangle-up
- Final exit: green circle
- Limit-up exit: orange triangle-down

**Private methods**: `_add_price_chart`, `_add_ratio_chart`, `_add_growth_rate_chart`, `_add_orderbook_thickness_chart`, `_add_balance_ratio_chart`, `_add_all_orders_io_ratio_chart`, `_add_large_orders_io_ratio_chart`, `_update_layout`, `_save_chart`

**PNG export**: Uses `kaleido` library (optional). Writes at 1920x1080.

#### Standalone function: `create_exit_visualization(df, trades, output_path, config, png_output_path) -> str`
- Compatibility wrapper around ChartCreator.

---

### 2.21 Visualization Module: `visualization/strategy_plot.py`

**Purpose**: Alternative visualization module with a different chart layout (3 subplots: price, ratio, change %).

**Standalone functions**:
- `get_tick(price: float) -> float`: Tick size lookup
- `calc_limit_up_price(ref_price: float, factor: float = 1.1) -> float`: Limit-up calculation
- `create_strategy_figure(stock_id, sector_name, price_data, events, ref_price, ratio_data, day_high_data, exhaustion_info) -> Figure`: Creates Plotly figure
- `create_strategy_html(stock_id, sector_name, price_data, events, ref_price, output_path, ratio_data, day_high_data, exhaustion_info)`: Creates HTML with trade summary table
- `_generate_trade_summary(events, ref_price, exhaustion_info) -> str`: Generates HTML summary table

**Event types in `events` list**: "entry", "partial_exit", "supplement", "exit"

---

## 3. Data Flow

### 3.1 Single Stock Backtest Flow

```
1. CLI arguments parsed
   |
2. BacktestEngine created with YAML config
   |
3. run_single_backtest(stock_id, date) called
   |
4. DataProcessor.load_feature_data(stock_id, date)
   |-- Reads: D:/feature_data/feature/{date}/{stock_id}.parquet
   |-- Converts 'time' column to DateTime
   |-- Filters: 09:00 <= time <= 13:30
   |
5. DataProcessor.process_trade_data(df)
   |-- Separates Trade and Depth rows
   |-- Replaces 0 with NaN in order book columns
   |-- Concatenates and sorts by time
   |-- Forward-fills order book columns
   |-- Keeps only Trade rows
   |-- Computes bid_volume_5level and ask_volume_5level
   |
6. DataProcessor.get_reference_price(stock_id, date)
   |-- Reads close.parquet
   |-- Finds last trading day before 'date'
   |-- Returns previous close price
   |
7. DataProcessor.calculate_limit_up(ref_price)
   DataProcessor.calculate_limit_down(ref_price)
   |
8. BacktestLoop.run(df, stock_id, ref_price, limit_up_price)
   |-- Initialize state and metrics
   |-- FOR EACH ROW (tick):
   |   |-- Update state from row
   |   |-- Update indicators (momentum, volume, matching, IO ratio)
   |   |-- Calculate metrics
   |   |-- IF has_position:
   |   |   |-- Check limit-up exit
   |   |   |-- Check trailing stop (if enabled)
   |   |   |-- Check entry price protection
   |   |   |-- Check momentum exhaustion (if trailing disabled)
   |   |   |-- Check final exit (if partial done)
   |   |   |-- Check hard stop-loss
   |   |   |-- Check reentry (if enabled and partial done)
   |   |-- ELSE IF price > 0:
   |   |   |-- Check Day High breakout -> enter waiting state
   |   |   |-- If waiting and got outside tick -> check entry conditions
   |   |   |-- If all conditions pass -> execute entry
   |   |-- Update prev_day_high
   |-- Force close at market close
   |-- Attach metrics to DataFrame
   |-- Return trade_history
   |
9. ReportGenerator.generate_report(df, trades, stock_id, date)
   |
10. TradeExporter.export_trade_details_to_csv(trades, stock_id, date)
   |
11. ChartCreator.create_strategy_chart(df, trades, ...)
    |-- HTML output: {output_path}/{date}/{stock_id}_{date}_strategy.html
    |-- PNG output: {output_path}/{date}/{stock_id}_{date}_strategy.png
```

### 3.2 Batch Backtest Flow

```
1. For each stock_id in stock_list:
   |-- run_single_backtest(stock_id, date, silent=not create_charts)
   |-- TradeStatisticsCalculator.calculate_statistics(stock_id, trades, date)
   |-- TradeDetailsProcessor.collect_trade_details(trades, stock_id, date)
   |
2. CSVExporter.export_detailed_trades_to_csv(all_trade_details, date)
   |
3. ReportGenerator.generate_summary_report(results, date)
```

### 3.3 Data Row Structure (from Parquet)

Each row in the feature parquet file contains:
```
time:           DateTime   -- timestamp to millisecond precision
price:          float      -- trade price (0 for depth-only rows)
volume:         float      -- trade volume in lots
tick_type:      int        -- 1=outside(buy), 2=inside(sell), 0=unknown
type:           string     -- "Trade" or "Depth"
day_high:       float      -- running day high price
bid_ask_ratio:  float      -- ask_5level / bid_5level (from depth data)
bid1_volume ... bid5_volume:  float -- bid order book levels
ask1_volume ... ask5_volume:  float -- ask order book levels
bid_volume_5level:  float  -- sum of bid levels
ask_volume_5level:  float  -- sum of ask levels
ratio_15s_300s: float      -- 15s/300s outside volume ratio indicator
pct_2min:       float      -- 2-minute price change %
pct_3min:       float      -- 3-minute price change %
pct_5min:       float      -- 5-minute price change %
low_1m:         float      -- 1-minute rolling low
low_3m:         float      -- 3-minute rolling low
low_5m:         float      -- 5-minute rolling low
low_10m:        float      -- 10-minute rolling low
low_15m:        float      -- 15-minute rolling low (optional, may be calculated)
```

---

## 4. Python to C# Mapping Guide

### 4.1 DataFrame -> C# Equivalents

| Python | C# Recommendation | Notes |
|--------|-------------------|-------|
| `pd.DataFrame` (main tick data) | `List<TickData>` where `TickData` is a custom record/class | Fast iteration, type-safe |
| `df.itertuples()` -> dict per row | `foreach (var tick in tickDataList)` | Direct property access |
| `df[df['type'] == 'Trade']` | `tickDataList.Where(t => t.Type == "Trade").ToList()` | LINQ |
| `df.sort_values('time')` | `tickDataList.OrderBy(t => t.Time).ToList()` | LINQ |
| `df[col].ffill()` | Custom forward-fill loop or extension method | No built-in equivalent |
| `pd.read_parquet(path)` | `Parquet.Net` or `Apache.Arrow` NuGet package | Read Parquet files |
| `pd.to_datetime()` | `DateTime.Parse()` or `DateTimeOffset.Parse()` | Built-in |
| `df.to_csv()` | `CsvHelper` NuGet package or manual `StreamWriter` | For CSV output |

**Recommended TickData class**:
```csharp
public class TickData
{
    public DateTime Time { get; set; }
    public double Price { get; set; }
    public double Volume { get; set; }
    public int TickType { get; set; }        // 1=outside, 2=inside
    public string Type { get; set; }          // "Trade" or "Depth"
    public double DayHigh { get; set; }
    public double BidAskRatio { get; set; }
    public double Bid1Volume { get; set; }
    // ... bid2-5, ask1-5 ...
    public double BidVolume5Level { get; set; }
    public double AskVolume5Level { get; set; }
    public double Ratio15s300s { get; set; }
    public double Pct2Min { get; set; }
    public double Pct3Min { get; set; }
    public double Pct5Min { get; set; }
    public double Low1m { get; set; }
    public double Low3m { get; set; }
    public double Low5m { get; set; }
    public double Low10m { get; set; }
    public double Low15m { get; set; }

    // Computed metrics (filled during backtest loop)
    public double DayHighGrowthRate { get; set; }
    public double BidAvgVolume { get; set; }
    public double AskAvgVolume { get; set; }
    public double BalanceRatio { get; set; }
    public bool DayHighBreakout { get; set; }
    public double InsideOutsideRatio { get; set; }
    public double OutsideRatio { get; set; }
    public double LargeOrderIoRatio { get; set; }
    public double LargeOrderOutsideRatio { get; set; }
}
```

### 4.2 YAML Config -> C# Config

| Python | C# Recommendation |
|--------|-------------------|
| `yaml.safe_load(file)` | `YamlDotNet` NuGet package |
| Dict-based config | Strongly-typed `StrategyConfig` class hierarchy |
| `config.get('key', default)` | Property with default in constructor or `??` operator |

**Recommended approach**: Define C# config classes that mirror the YAML structure and deserialize with YamlDotNet.

```csharp
public class StrategySection
{
    public string StrategyMode { get; set; } = "B";
    public string EntryStartTime { get; set; } = "09:05:00";
    public string EntryCutoffTime { get; set; } = "13:00:00";
    public double EntryCooldown { get; set; } = 30.0;
    // ... etc.
    public TrailingStopConfig TrailingStop { get; set; } = new();
}

public class TrailingStopConfig
{
    public bool Enabled { get; set; } = false;
    public List<TrailingStopLevel> Levels { get; set; } = new();
    public bool EntryPriceProtection { get; set; } = true;
}

public class TrailingStopLevel
{
    public string Name { get; set; }   // "1min", "3min", "5min"
    public string Field { get; set; }  // "low_1m", "low_3m", "low_5m"
    public double ExitRatio { get; set; } = 0.333;
}
```

### 4.3 Python Collections -> C#

| Python | C# |
|--------|-----|
| `dict` | `Dictionary<string, object>` or strongly typed class |
| `list` | `List<T>` |
| `deque` | `LinkedList<T>` or `Queue<T>` (for FIFO with removal from front) |
| `deque(maxlen=N)` | Custom circular buffer or check size after add |
| `Optional[T]` | `T?` (nullable) |
| `Tuple[float, float]` | `(double, double)` or custom struct |
| `dataclass` | `record` or `class` with properties |

### 4.4 Decimal Arithmetic

Python uses `decimal.Decimal` for tick size / limit price calculations. In C#:
```csharp
// Use decimal for price calculations to avoid floating-point issues
decimal rawLimit = (decimal)previousClose * 1.10m;
decimal tickSize = GetTickSizeDecimal((double)rawLimit);
decimal limitUp = Math.Floor(rawLimit / tickSize) * tickSize;
```

### 4.5 Visualization

| Python | C# Options |
|--------|-----------|
| `plotly` (interactive HTML) | **ScottPlot** (static PNG), **Plotly.NET** (via F# interop or direct JSON), **LiveCharts2**, or simply output JSON for a separate web viewer |
| `plotly.graph_objects.Scatter` | Depends on chosen library |
| `make_subplots` | Depends on chosen library |
| `fig.write_html()` | Generate HTML with embedded Plotly.js JSON |
| `pio.write_image()` (PNG via kaleido) | ScottPlot .SaveFig() or OxyPlot export |

**Recommended**: Use **Plotly.NET** NuGet package for HTML output (closest to the Python original), or generate the Plotly JSON manually and embed it in an HTML template. For PNG, use **ScottPlot** or **OxyPlot**.

### 4.6 Logging

| Python | C# |
|--------|-----|
| `logging.getLogger(__name__)` | `ILogger<T>` with Microsoft.Extensions.Logging, or `NLog`, or `Serilog` |
| `logger.info(...)` | `_logger.LogInformation(...)` |
| `logger.warning(...)` | `_logger.LogWarning(...)` |

### 4.7 File I/O

| Python | C# |
|--------|-----|
| `pd.read_parquet(path)` | `Parquet.Net` (`ParquetReader`) or `Apache.Arrow` |
| `pd.read_csv(path)` | `CsvHelper` or `File.ReadAllLines` + split |
| `os.path.exists()` | `File.Exists()` or `Directory.Exists()` |
| `os.makedirs(dir, exist_ok=True)` | `Directory.CreateDirectory(dir)` |
| `json.dump(obj, file)` | `System.Text.Json.JsonSerializer.Serialize()` |

### 4.8 DateTime Handling

| Python | C# |
|--------|-----|
| `datetime.strptime(s, '%H:%M:%S').time()` | `TimeOnly.Parse(s)` (.NET 6+) or `TimeSpan.Parse(s)` |
| `current_time.time()` | `currentTime.TimeOfDay` or `TimeOnly.FromDateTime(currentTime)` |
| `(t2 - t1).total_seconds()` | `(t2 - t1).TotalSeconds` |
| `timedelta(seconds=N)` | `TimeSpan.FromSeconds(N)` |

---

## 5. C# Project Structure

```
BoReentryBacktest/                         (Solution)
|
|-- BoReentryBacktest.Core/               (Class Library)
|   |-- Constants.cs                       // All constants
|   |-- BacktestEngine.cs                 // Main orchestrator
|   |-- BacktestLoop.cs                   // Tick-by-tick loop
|   |-- Models/
|   |   |-- TickData.cs                   // Data row model
|   |   |-- LoopState.cs                  // Loop state
|   |   |-- BreakoutBufferState.cs
|   |   |-- WaitingForOutsideEntryState.cs
|   |   |-- MetricsAccumulator.cs
|   |
|-- BoReentryBacktest.Strategy/           (Class Library)
|   |-- Config/
|   |   |-- StrategyConfig.cs             // Config loader + typed config
|   |   |-- EntryConfig.cs
|   |   |-- ExitConfig.cs
|   |   |-- ReentryConfig.cs
|   |   |-- TrailingStopConfig.cs
|   |-- Data/
|   |   |-- DataProcessor.cs              // Parquet loading, processing
|   |   |-- TickSizeHelper.cs             // Tick size / limit price calculations
|   |-- Entry/
|   |   |-- EntryChecker.cs
|   |   |-- EntrySignal.cs
|   |   |-- SmallOrderFilter.cs
|   |-- Exit/
|   |   |-- ExitManager.cs
|   |   |-- ExitResult.cs
|   |-- Position/
|   |   |-- Position.cs
|   |   |-- TradeRecord.cs
|   |   |-- PositionManager.cs
|   |   |-- ReentryManager.cs
|   |-- Indicators/
|   |   |-- DayHighMomentumTracker.cs
|   |   |-- OrderBookBalanceMonitor.cs
|   |   |-- OutsideVolumeTracker.cs
|   |   |-- MassiveMatchingTracker.cs
|   |   |-- InsideOutsideRatioTracker.cs
|   |
|-- BoReentryBacktest.Analytics/          (Class Library)
|   |-- TradeStatisticsCalculator.cs
|   |-- ReportGenerator.cs
|   |-- TradeDetailsProcessor.cs
|   |
|-- BoReentryBacktest.Exporters/          (Class Library)
|   |-- CsvExporter.cs
|   |-- TradeExporter.cs
|   |-- EarlyEntryExporter.cs
|   |-- EarlyChartCreator.cs
|   |
|-- BoReentryBacktest.Visualization/      (Class Library)
|   |-- ChartCreator.cs
|   |-- StrategyPlot.cs
|   |
|-- BoReentryBacktest.Console/            (Console App)
|   |-- Program.cs                        // CLI entry point
|   |-- Bo_v2.yaml                        // Config file (embedded/copied)
```

### Namespace Convention
```
BoReentryBacktest.Core
BoReentryBacktest.Strategy.Config
BoReentryBacktest.Strategy.Data
BoReentryBacktest.Strategy.Entry
BoReentryBacktest.Strategy.Exit
BoReentryBacktest.Strategy.Position
BoReentryBacktest.Strategy.Indicators
BoReentryBacktest.Analytics
BoReentryBacktest.Exporters
BoReentryBacktest.Visualization
```

---

## 6. Key Constants and Enums

### 6.1 Constants (for `Constants.cs`)

```csharp
public static class Constants
{
    // Paths
    public const string OutputBaseDir = @"D:\backtest_results";
    public const string ScreeningResultsPath = @"C:\Users\User\Documents\_02_bt\Backtest_tick_module\screening_results.csv";
    public const string DefaultConfigPath = "Bo_v2.yaml";

    // Trading
    public const int DefaultSharesPerTrade = 1000;
    public const double MaxGainPercentage = 8.5;
    public static readonly TimeOnly MarketOpenTimeLimit = new(9, 5, 0);
    public static readonly TimeOnly MarketCloseTime = new(13, 30, 0);

    // Indicator Windows (seconds)
    public const int DayHighMomentumWindow = 60;
    public const int OutsideVolumeWindow = 3;
    public const int MassiveMatchingWindow = 1;
    public const int IoRatioWindow = 60;

    // Order Book
    public const int LargeOrderThreshold = 10;
    public const int OrderBookThinThreshold = 20;
    public const int OrderBookNormalThreshold = 40;

    // Buffer
    public const int BufferDurationSeconds = 3;

    // Logging
    public const string LogFormat = "{Timestamp:HH:mm:ss} [{Level}] {Message}{NewLine}";
}
```

### 6.2 Enums

```csharp
public enum TickType
{
    Unknown = 0,
    Outside = 1,   // Buy / aggressive buy
    Inside = 2     // Sell / aggressive sell
}

public enum DataRowType
{
    Trade,
    Depth
}

public enum StrategyMode
{
    A,  // Exhaustion-based exit
    B   // Simple trend / trailing stop
}

public enum ExitType
{
    Partial,        // First stage partial exit (50%)
    Remaining,      // Close remaining position
    TrailingStop,   // Trailing stop level triggered
    Protection,     // Entry price protection
    ReentryStop,    // Reentry position stop-loss
    MarketClose     // Forced close at market close
}

public enum TrailingStopLevel
{
    OneMinute,      // "1min" -> low_1m
    ThreeMinute,    // "3min" -> low_3m
    FiveMinute      // "5min" -> low_5m
}

public enum ExitReason
{
    TickStopLoss,
    LimitUp,
    MomentumExhaustion,
    TrailingStop1Min,
    TrailingStop3Min,
    TrailingStop5Min,
    EntryPriceProtection,
    MarketClose,
    FinalExit3MinLow,
    FinalExitEntryPrice
}
```

---

## 7. Interface Definitions

### 7.1 Core Interfaces

```csharp
public interface IBacktestEngine
{
    List<TradeRecord> RunSingleBacktest(string stockId, string date, bool silent = false);
    List<Dictionary<string, object>> RunBatchBacktest(
        List<string> stockList, string date,
        bool outputCsv = true, bool createCharts = true);
}

public interface IDataProcessor
{
    List<TickData> LoadFeatureData(string stockId, string date);
    List<TickData> ProcessTradeData(List<TickData> rawData);
    double? GetReferencePrice(string stockId, string date, string closePath = null);
    string GetCompanyName(string stockId);
    bool ValidateData(List<TickData> data);

    static abstract decimal GetTickSizeDecimal(double price);
    static abstract double CalculateLimitUp(double previousClose);
    static abstract double CalculateLimitDown(double previousClose);
}

public interface IEntryChecker
{
    bool CheckDayHighBreakout(double currentDayHigh, double prevDayHigh);
    EntrySignal CheckEntrySignals(
        string stockId, double currentPrice, DateTime currentTime,
        double prevDayHigh, Dictionary<string, double> indicators,
        double askBidRatio, double refPrice,
        double massiveMatchingAmount,
        double? fixedRatioThreshold, double? dynamicRatioThreshold,
        double? minOutsideAmount, bool forceLog,
        List<TickData> tickData, TickData currentRow,
        bool isDayHighBreakout, DateTime? lastExitTime);
}

public interface IExitManager
{
    ExitResult CheckHardStopLoss(Position position, double currentPrice, DateTime currentTime);
    ExitResult CheckLimitUpExit(Position position, double currentPrice, DateTime currentTime, double limitUpPrice);
    ExitResult CheckMomentumExhaustion(Position position, TickData row,
        DayHighMomentumTracker momentumTracker, OrderBookBalanceMonitor orderbookMonitor,
        DateTime currentTime, double currentPrice);
    ExitResult CheckTrailingStop(Position position, TickData row, DateTime currentTime, double currentPrice);
    ExitResult CheckEntryPriceProtection(Position position, double currentPrice, DateTime currentTime);
    ExitResult CheckFinalExit(Position position, TickData row, DateTime currentTime, double currentPrice);
}

public interface IPositionManager
{
    void Reset();
    Position OpenPosition(DateTime entryTime, double entryPrice, double entryBidThickness,
        double dayHighAtEntry, double entryRatio, double entryOutsideVolume3s = 0.0);
    bool PartialExit(DateTime exitTime, double exitPrice, string exitReason);
    bool TrailingStopExit(DateTime exitTime, double exitPrice, double exitRatio,
        string exitLevel, string exitReason);
    TradeRecord ClosePosition(DateTime exitTime, double exitPrice, string exitReason,
        bool isReentryExit = false);
    bool HasPosition { get; }
    Position CurrentPosition { get; }
    List<TradeRecord> TradeHistory { get; }
}

public interface IIndicatorTracker
{
    void Reset();
}

public interface ITradeStatisticsCalculator
{
    Dictionary<string, object> CalculateStatistics(string stockId, List<TradeRecord> trades, string date);
}

public interface IReportGenerator
{
    void GenerateReport(List<TickData> data, List<TradeRecord> trades, string stockId, string date);
    object GenerateSummaryReport(List<Dictionary<string, object>> results, string date);
}

public interface ICsvExporter
{
    void ExportSummaryToCsv(List<Dictionary<string, object>> results, string date);
    void ExportDetailedTradesToCsv(List<Dictionary<string, object>> allTradeDetails, string date);
}

public interface IChartCreator
{
    string CreateStrategyChart(List<TickData> data, List<Dictionary<string, object>> trades,
        string outputPath, string pngOutputPath = null,
        double? refPrice = null, double? limitUpPrice = null,
        string stockId = null, string companyName = null);
}
```

### 7.2 ExitResult Class

```csharp
public class ExitResult
{
    public ExitType ExitType { get; set; }
    public double ExitRatio { get; set; }
    public string ExitReason { get; set; }
    public double ExitPrice { get; set; }
    public DateTime ExitTime { get; set; }
    public string ExitLevel { get; set; }  // For trailing stop

    // Optional diagnostic fields
    public double? StopLossPrice { get; set; }
    public int? StopLossTicks { get; set; }
    public double? GrowthRate { get; set; }
    public double? BalanceRatio { get; set; }
    public double? GrowthDrawdown { get; set; }
    public double? ExitThreshold { get; set; }
}
```

---

## 8. Configuration Schema

### 8.1 Full Config Hierarchy (for YAML deserialization)

```csharp
public class BacktestConfig
{
    public StrategySection Strategy { get; set; } = new();
    public ClosePricesSection ClosePrices { get; set; } = new();
    public OutputSection Output { get; set; } = new();
    public DataSection Data { get; set; } = new();
}

public class StrategySection
{
    // Strategy mode
    public string StrategyMode { get; set; } = "B";

    // Time settings
    public string EntryStartTime { get; set; } = "09:05:00";
    public string EntryCutoffTime { get; set; } = "13:00:00";
    public double EntryCooldown { get; set; } = 30.0;

    // Entry conditions
    public bool AboveOpenCheckEnabled { get; set; } = false;
    public double AskBidRatioThreshold { get; set; } = 1.0;
    public bool MassiveMatchingEnabled { get; set; } = true;
    public double MassiveMatchingAmount { get; set; } = 50_000_000;
    public bool UseDynamicLiquidityThreshold { get; set; } = false;
    public string DynamicLiquidityThresholdFile { get; set; } = "daily_liquidity_threshold.parquet";
    public double DynamicLiquidityMultiplier { get; set; } = 0.004;
    public double DynamicLiquidityThresholdCap { get; set; } = 50_000_000;
    public bool RatioEntryEnabled { get; set; } = true;
    public double RatioEntryThreshold { get; set; } = 3.0;
    public string RatioColumn { get; set; } = "ratio_15s_300s";

    // Interval filter
    public bool IntervalPctFilterEnabled { get; set; } = true;
    public int IntervalPctMinutes { get; set; } = 5;
    public double IntervalPctThreshold { get; set; } = 3.0;

    // Breakout quality
    public bool BreakoutQualityCheckEnabled { get; set; } = false;
    public int BreakoutMinVolume { get; set; } = 10;
    public double BreakoutMinAskEatRatio { get; set; } = 0.25;
    public int BreakoutAbsoluteLargeVolume { get; set; } = 50;

    // Small order filter
    public bool SmallOrderFilterEnabled { get; set; } = false;
    public int SmallOrderCheckTrades { get; set; } = 30;
    public int SmallOrderThreshold { get; set; } = 3;
    public int TinyOrderThreshold { get; set; } = 2;
    public int SingleOrderThreshold { get; set; } = 1;
    public double SmallOrderRatioLimit { get; set; } = 0.8;
    public double TinyOrderRatioLimit { get; set; } = 0.5;
    public double SingleOrderRatioLimit { get; set; } = 0.4;
    public bool RequireLargeOrderConfirmation { get; set; } = false;
    public int LargeOrderThreshold { get; set; } = 20;
    public int MinLargeOrders { get; set; } = 2;

    // Buffer mechanism
    public bool EntryBufferEnabled { get; set; } = true;
    public int EntryBufferMilliseconds { get; set; } = 100;

    // Low point filters
    public bool Low10MinFilterEnabled { get; set; } = false;
    public double Low10MinThreshold { get; set; } = 4.0;

    // Reentry
    public bool Reentry { get; set; } = false;
    public string TimeSettings { get; set; } = "3s";

    // Output
    public string OutputPath { get; set; } = "D:/backtest_results";

    // Grace period
    public double GracePeriod { get; set; } = 3.0;

    // Stop-loss
    public int PriceStopLossTicks { get; set; } = 3;
    public int StrategyBStopLossTicksSmall { get; set; } = 3;
    public int StrategyBStopLossTicksLarge { get; set; } = 2;
    public double MomentumStopLossSeconds { get; set; } = 15.0;
    public double MomentumStopLossSecondsExtended { get; set; } = 30.0;
    public double MomentumStopLossAmount { get; set; } = 5_000_000;
    public double MomentumExtendedCapitalThreshold { get; set; } = 50_000_000_000;
    public double MomentumExtendedPriceMin { get; set; } = 100.0;
    public double MomentumExtendedPriceMax { get; set; } = 500.0;

    // Trailing stop
    public TrailingStopConfig TrailingStop { get; set; } = new();

    // Ratio increase after loss
    public bool RatioIncreaseAfterLossEnabled { get; set; } = true;
    public double RatioIncreaseMultiplier { get; set; } = 0.8;
    public double RatioIncreaseMinThreshold { get; set; } = 6.0;

    // Stop-loss limits
    public bool StopLossLimitEnabled { get; set; } = false;
    public int MaxStopLossCount { get; set; } = 2;

    // Day high drawdown
    public double DayHighDrawdownThreshold { get; set; } = 0.0086;

    // Exhaustion settings (Strategy A)
    public double PeakTrackingDelay { get; set; } = 3.0;
    public double ExhaustionDrawdown { get; set; } = 0.20;
    public int ExhaustionPriceDrawdownTicks { get; set; } = 2;
}

public class ClosePricesSection
{
    public string File { get; set; } = "close.parquet";
}

public class OutputSection
{
    public bool RecordTicks { get; set; } = true;
    public string OutputDir { get; set; } = "logs";
    public bool GenerateHtml { get; set; } = true;
    public bool GeneratePng { get; set; } = true;
    public string PngOutputDir { get; set; } = @"D:\backtest_results";
    public bool SaveBacktestData { get; set; } = true;
    public string BacktestDataDir { get; set; } = @"D:\backtest_results";
}

public class DataSection
{
    public string FeatureDir { get; set; }  // Optional override
}
```

---

## 9. Critical Business Logic Details

### 9.1 Taiwan Stock Exchange Tick Size Rules

This is used in MULTIPLE places (ExitManager, PositionManager, DataProcessor, StrategyPlot). Extract into a shared utility:

```csharp
public static class TickSizeHelper
{
    public static double GetTickSize(double price)
    {
        if (price >= 1000) return 5.0;
        if (price >= 500) return 1.0;
        if (price >= 100) return 0.5;
        if (price >= 50) return 0.1;
        if (price >= 10) return 0.05;
        return 0.01;
    }

    public static decimal GetTickSizeDecimal(double price)
    {
        if (price >= 1000) return 5m;
        if (price >= 500) return 1m;
        if (price >= 100) return 0.5m;
        if (price >= 50) return 0.1m;
        if (price >= 10) return 0.05m;
        return 0.01m;
    }

    public static double AddTicks(double price, int ticks)
    {
        return price + GetTickSize(price) * ticks;
    }

    public static double CalculateLimitUp(double previousClose)
    {
        decimal raw = (decimal)previousClose * 1.10m;
        decimal tick = GetTickSizeDecimal((double)raw);
        decimal limitUp = Math.Floor(raw / tick) * tick;
        return (double)Math.Round(limitUp, 2);
    }

    public static double CalculateLimitDown(double previousClose)
    {
        decimal raw = (decimal)previousClose * 0.90m;
        decimal tick = GetTickSizeDecimal((double)raw);
        decimal limitDown = Math.Ceiling(raw / tick) * tick;  // Note: Python uses a different rounding approach
        return (double)limitDown;
    }
}
```

**Note on limit-down calculation**: The Python code uses `((raw + tick - 0.01) // tick) * tick` which is an integer-division-based ceiling. In C# use `Math.Ceiling(raw / tick) * tick` but verify edge cases match.

### 9.2 Hard Stop-Loss Logic

The hard stop-loss uses **different tick counts based on tick size category**:

```
entry_tick_size = GetTickSize(position.DayHighAtEntry)

if tick_size is 0.5 or 5.0 (large tick group):
    stop_loss_ticks = config.StrategyBStopLossTicksLarge  (default: 2)
else (small tick group: 0.01, 0.05, 0.1, 1.0):
    stop_loss_ticks = config.StrategyBStopLossTicksSmall  (default: 3)

stop_loss_price = DayHighAtEntry - (tick_size * stop_loss_ticks)
if current_price <= stop_loss_price: TRIGGER STOP-LOSS
```

### 9.3 Entry "Waiting for Outside Tick" State Machine

This is the most subtle piece of logic. After a Day High breakout is detected:

1. **Breakout detected**: `current_day_high > prev_day_high && prev_day_high > 0`
2. **Enter waiting state**: Record breakout time and day_high
3. **On each subsequent tick**:
   - If tick_type == 1 (outside) AND tick.time != breakout_time: proceed to check all entry conditions
   - If tick_type != 1 OR tick.time == breakout_time: stay in waiting state
   - If a NEW breakout occurs while waiting: update the waiting state (new breakout time/day_high)
4. **Entry conditions checked only when waiting state resolves to an outside tick**

### 9.4 Trailing Stop Multi-Level Exit

When trailing stop is enabled, the position exits in three stages:

1. **Level 1 (1min low)**: Exit 33.3% when price <= low_1m
2. **Level 2 (3min low)**: Exit 33.3% when price <= low_3m
3. **Level 3 (5min low)**: Exit 33.4% when price <= low_5m

Each level can only trigger once. After all levels trigger (remaining_ratio <= 0), the position is fully closed with a weighted average exit price.

**Entry Price Protection**: If enabled and remaining_ratio < 1.0 (i.e., some trailing stop has fired), if price drops to entry_price or below, exit ALL remaining position immediately.

### 9.5 PnL Calculation

For trailing stop mode:
```
total_pnl = sum of ((exit_price_i - entry_price) / entry_price * 100 * exit_ratio_i)
          + ((final_exit_price - entry_price) / entry_price * 100 * remaining_ratio)
```

For two-stage mode:
```
total_pnl = ((partial_exit_price - entry_price) / entry_price * 100 * 0.5)
          + ((final_exit_price - entry_price) / entry_price * 100 * 0.5)
```

### 9.6 Ratio Threshold Escalation After Stop-Loss

When `ratio_increase_after_loss_enabled` is true and the previous exit was a stop-loss:

```python
threshold_candidates = []
if min_threshold is not None:
    threshold_candidates.append(min_threshold)  # e.g., 6.0
if last_entry_ratio > 0:
    threshold_candidates.append(last_entry_ratio)  # e.g., 4.5

threshold = min(threshold_candidates)  # Take the smaller of the two
threshold = max(default_threshold, threshold)  # But at least the default (e.g., 3.0)
```

This means after a stop-loss, the next entry requires a higher ratio.

### 9.7 Forward-Fill Logic for Order Book Data

Trade rows have price/volume data but NO order book data. Depth rows have order book data but NO trade price. The system:

1. Sets all order book columns in Trade rows to NaN (replacing 0)
2. Concatenates Trade + Depth rows, sorted by time
3. Forward-fills order book columns
4. Keeps only Trade rows

This ensures each Trade row has the most recent order book snapshot.

### 9.8 Volume Amount Calculation

Outside volume tracking converts lots to monetary amount:
```
trade_amount = price * volume_lots * 1000
```
Where 1 lot = 1000 shares. So the amount is `price_per_share * total_shares`.

### 9.9 Commission Calculation (Early Entry Exporter only)

```
position_value = entry_price * total_shares  // e.g., 12 * 1000 = 12000 shares
commission = position_value * 0.0017         // 0.17% brokerage fee
profit = exit_value - position_value - commission
return_rate = (profit / position_value) * 100
```

---

## Appendix A: NuGet Package Recommendations

| Purpose | Package | Notes |
|---------|---------|-------|
| YAML parsing | `YamlDotNet` | Deserialize YAML to C# objects |
| Parquet reading | `Parquet.Net` or `Apache.Arrow` | Read .parquet files |
| CSV export | `CsvHelper` | Write CSV with headers |
| Charting (HTML) | `Plotly.NET` | Generate interactive HTML charts |
| Charting (PNG) | `ScottPlot` or `OxyPlot` | Static image generation |
| CLI parsing | `System.CommandLine` or `CommandLineParser` | Parse CLI arguments |
| Logging | `Serilog` or `Microsoft.Extensions.Logging` | Structured logging |
| JSON | `System.Text.Json` | Built-in, or `Newtonsoft.Json` |

---

## Appendix B: File Dependencies Graph

```
bo_reentry.py
  -> core/backtest_engine.py
       -> strategy_modules/config_loader.py
       -> strategy_modules/data_processor.py
       -> strategy_modules/entry_logic.py
            -> strategy_modules/small_order_filter.py
       -> strategy_modules/exit_logic.py
       -> strategy_modules/position_manager.py
       -> strategy_modules/indicators.py
       -> core/backtest_loop.py
            -> strategy_modules/entry_logic.py
            -> strategy_modules/exit_logic.py
            -> strategy_modules/position_manager.py
       -> analytics/trade_statistics.py
            -> strategy_modules/position_manager.py
       -> analytics/report_generator.py
            -> strategy_modules/position_manager.py
       -> analytics/trade_details.py
            -> strategy_modules/position_manager.py
       -> exporters/csv_exporter.py
       -> exporters/trade_exporter.py
            -> analytics/trade_details.py
       -> visualization/chart_creator.py
  -> core/constants.py
```

---

## Appendix C: Work Division Suggestion

### Teammate 2 (Core + Strategy):
- `BoReentryBacktest.Core`: Constants, BacktestEngine, BacktestLoop, TickData model, LoopState
- `BoReentryBacktest.Strategy`: All config, data processing, entry/exit logic, position management, indicators, small order filter
- `BoReentryBacktest.Console`: Program.cs entry point

### Teammate 3 (Analytics + Exporters + Visualization):
- `BoReentryBacktest.Analytics`: TradeStatisticsCalculator, ReportGenerator, TradeDetailsProcessor
- `BoReentryBacktest.Exporters`: CsvExporter, TradeExporter, EarlyEntryExporter, EarlyChartCreator
- `BoReentryBacktest.Visualization`: ChartCreator, StrategyPlot

### Shared Types (define first, used by all):
- `TickData`, `Position`, `TradeRecord`, `EntrySignal`, `ExitResult`
- All interfaces from Section 7
- All enums from Section 6
- `TickSizeHelper` utility class
- Config classes from Section 8

---

*End of Design Document*
