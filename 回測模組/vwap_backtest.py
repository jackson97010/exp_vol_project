"""Adaptive VWAP 回測系統 v2.0 -- 主進入點

支援兩種操作模式：

1. **命令列模式** (backward compatible)::

    # 單股回測
    python vwap_backtest.py --mode single --stock_id 2330 --date 2024-01-15

    # 批次回測
    python vwap_backtest.py --mode batch --stock_list 2330 2454 --date 2024-01-15

    # 覆蓋設定參數
    python vwap_backtest.py --mode single --stock_id 2330 --date 2024-01-15 \
        --total_volume 20000000 --spread 0.002

    # 跳過 CSV / 圖表
    python vwap_backtest.py --mode single --stock_id 2330 --date 2024-01-15 --no_csv --no_chart

2. **互動式選單** (無引數時自動啟動)::

    python vwap_backtest.py
"""

from __future__ import annotations

import argparse
import glob
import logging
import os
import re
import sys
from typing import Dict, List, Optional

import yaml

from core.vwap_engine import VWAPEngine
from strategy_modules.data_processor import DataProcessor
from strategy_modules.vwap_models import VWAPMetrics

# ---------------------------------------------------------------------------
# 常數
# ---------------------------------------------------------------------------

_FEATURE_DATA_DIR = "D:/feature_data/feature"
_DEFAULT_CONFIG_PATH = "vwap_config.yaml"

logger = logging.getLogger(__name__)


# ===========================================================================
# 小工具 / Helper functions
# ===========================================================================


def _setup_logging() -> None:
    """設定日誌格式。"""
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
        datefmt="%H:%M:%S",
    )


def detect_latest_date() -> Optional[str]:
    """掃描 feature_data 目錄，回傳最新的日期字串 (YYYY-MM-DD)。

    Returns:
        最新日期字串，若無任何日期資料夾則回傳 ``None``。
    """
    if not os.path.isdir(_FEATURE_DATA_DIR):
        return None

    date_pattern = re.compile(r"^\d{4}-\d{2}-\d{2}$")
    dates: List[str] = []
    for entry in os.listdir(_FEATURE_DATA_DIR):
        if date_pattern.match(entry):
            full = os.path.join(_FEATURE_DATA_DIR, entry)
            if os.path.isdir(full):
                dates.append(entry)

    if not dates:
        return None

    dates.sort()
    return dates[-1]


def list_available_dates_for_stock(stock_id: str, limit: int = 5) -> List[str]:
    """列出某檔股票有資料的最近 *limit* 個日期 (降冪排列)。

    Args:
        stock_id: 股票代碼。
        limit: 最多回傳幾筆。

    Returns:
        日期字串清單 (最新在前)。
    """
    pattern = os.path.join(_FEATURE_DATA_DIR, "*", f"{stock_id}.parquet")
    matches = glob.glob(pattern)

    dates: List[str] = []
    date_re = re.compile(r"^\d{4}-\d{2}-\d{2}$")
    for path in matches:
        dir_name = os.path.basename(os.path.dirname(path))
        if date_re.match(dir_name):
            dates.append(dir_name)

    dates.sort(reverse=True)
    return dates[:limit]


def list_stocks_for_date(date: str) -> List[str]:
    """列出某日期目錄下所有可用的股票代碼。

    Args:
        date: 日期字串 (YYYY-MM-DD)。

    Returns:
        股票代碼清單 (已排序)。
    """
    date_dir = os.path.join(_FEATURE_DATA_DIR, date)
    if not os.path.isdir(date_dir):
        return []

    stocks: List[str] = []
    for fname in os.listdir(date_dir):
        if fname.endswith(".parquet"):
            stocks.append(fname.replace(".parquet", ""))

    stocks.sort()
    return stocks


def stock_data_exists(stock_id: str, date: str) -> bool:
    """檢查某股票在指定日期是否有資料。"""
    path = os.path.join(_FEATURE_DATA_DIR, date, f"{stock_id}.parquet")
    return os.path.isfile(path)


def date_dir_exists(date: str) -> bool:
    """檢查某日期目錄是否存在。"""
    return os.path.isdir(os.path.join(_FEATURE_DATA_DIR, date))


def parse_amount(raw: str) -> Optional[float]:
    """解析金額字串，支援純數字與中文萬/億單位。

    範例::

        parse_amount("5000000")   -> 5000000.0
        parse_amount("500萬")     -> 5000000.0
        parse_amount("1.5億")     -> 150000000.0
        parse_amount("1000萬")    -> 10000000.0

    Args:
        raw: 使用者輸入的金額字串。

    Returns:
        解析後的浮點數金額，失敗回傳 ``None``。
    """
    raw = raw.strip().replace(",", "").replace(" ", "")
    if not raw:
        return None

    # 嘗試「數字 + 萬/億」格式
    match = re.match(r"^([0-9]*\.?[0-9]+)\s*(萬|億)?$", raw)
    if not match:
        return None

    number = float(match.group(1))
    unit = match.group(2)

    if unit == "萬":
        number *= 10_000
    elif unit == "億":
        number *= 100_000_000

    if number <= 0:
        return None

    return number


def format_amount(value: float) -> str:
    """將金額格式化為帶千分位分隔的字串。

    Args:
        value: 金額數值。

    Returns:
        格式化後的字串，例如 ``"10,000,000"``。
    """
    return f"{value:,.0f}"


def _get_company_name(stock_id: str) -> str:
    """取得公司名稱（建立一個暫時的 DataProcessor 來查詢）。"""
    try:
        dp = DataProcessor({})
        return dp.get_company_name(stock_id)
    except Exception:
        return stock_id


# ===========================================================================
# Ctrl+C 攔截 — 回到主選單而非退出程式
# ===========================================================================


class _ReturnToMenu(Exception):
    """內部例外：Ctrl+C / EOF 時拋出，由主選單迴圈攔截。"""


def _safe_input(prompt: str) -> str:
    """包裝 input()，處理 KeyboardInterrupt 和 EOFError。

    Args:
        prompt: 顯示給使用者的提示文字。

    Returns:
        使用者輸入的字串 (已 strip)。

    Raises:
        _ReturnToMenu: 當 Ctrl+C 被按下或 EOF 時。
    """
    try:
        return input(prompt).strip()
    except (EOFError, KeyboardInterrupt):
        print()
        raise _ReturnToMenu


def _confirm(prompt: str, default_yes: bool = True) -> bool:
    """顯示 Y/N 確認提示。

    Args:
        prompt: 問題文字。
        default_yes: 預設為 Y 或 N。

    Returns:
        使用者選擇。
    """
    hint = "[Y/n]" if default_yes else "[y/N]"
    raw = _safe_input(f"{prompt} {hint}: ").lower()
    if not raw:
        return default_yes
    return raw in ("y", "yes")


# ===========================================================================
# 設定檢視 / 修改
# ===========================================================================


def _display_settings(config_path: str) -> Dict:
    """讀取 YAML 設定並以表格形式印出。

    Args:
        config_path: YAML 設定檔路徑。

    Returns:
        解析後的原始 dict (供後續修改使用)。
    """
    with open(config_path, "r", encoding="utf-8") as f:
        raw: Dict = yaml.safe_load(f) or {}

    vwap = raw.get("vwap", {})
    adaptive = raw.get("adaptive", {})

    total_vol = vwap.get("total_volume_quote", 10_000_000)
    start_t = vwap.get("start_time", "09:00:00")
    end_t = vwap.get("end_time", "13:25:00")
    max_lots = vwap.get("max_single_order_lots", 50)
    min_lots = vwap.get("min_order_lots", 1)
    spread = vwap.get("price_spread_levels", 3)

    adp_enabled = adaptive.get("enabled", True)
    ato_end = adaptive.get("ato_end_time", "09:05:00")
    order_start = adaptive.get("order_start_time", "09:10:00")
    mo_lo_thr = adaptive.get("mo_lo_threshold", 0.5)
    lookback = adaptive.get("volume_lookback_days", 20)

    print()
    print(f"--- 目前設定 ({config_path}) ---")
    print(f"  目標金額:       {format_amount(total_vol)} TWD")
    print(f"  執行窗口:       {start_t} ~ {end_t}")
    print(f"  單筆最大張數:   {max_lots}")
    print(f"  單筆最小張數:   {min_lots}")
    print(f"  深度檔位:       {spread}")
    print(f"  Adaptive:       {'ON' if adp_enabled else 'OFF'}")
    print(f"  ATO 結束:       {ato_end}")
    print(f"  下單開始:       {order_start}")
    print(f"  MO/LO 門檻:    {mo_lo_thr}")
    print(f"  回望天數:       {lookback}")
    print()

    return raw


def _edit_settings(config_path: str) -> None:
    """互動式修改設定，並寫回 YAML 檔案。

    按下 Enter 保留目前值。

    Args:
        config_path: YAML 設定檔路徑。
    """
    raw = _display_settings(config_path)

    if not _confirm("修改設定?", default_yes=False):
        return

    vwap = raw.setdefault("vwap", {})
    adaptive = raw.setdefault("adaptive", {})

    print()
    print("(按 Enter 保留目前值)")
    print()

    # --- 目標金額 ---
    current_total = vwap.get("total_volume_quote", 10_000_000)
    val = _safe_input(f"  目標金額 [{format_amount(current_total)}]: ")
    if val:
        parsed = parse_amount(val)
        if parsed is not None:
            vwap["total_volume_quote"] = int(parsed)
        else:
            print("  * 格式無法辨識，保留原值")

    # --- 開始時間 ---
    current_start = vwap.get("start_time", "09:00:00")
    val = _safe_input(f"  開始時間 [{current_start}]: ")
    if val:
        if re.match(r"^\d{2}:\d{2}:\d{2}$", val):
            vwap["start_time"] = val
        else:
            print("  * 格式應為 HH:MM:SS，保留原值")

    # --- 結束時間 ---
    current_end = vwap.get("end_time", "13:25:00")
    val = _safe_input(f"  結束時間 [{current_end}]: ")
    if val:
        if re.match(r"^\d{2}:\d{2}:\d{2}$", val):
            vwap["end_time"] = val
        else:
            print("  * 格式應為 HH:MM:SS，保留原值")

    # --- 單筆最大張數 ---
    current_max = vwap.get("max_single_order_lots", 50)
    val = _safe_input(f"  單筆最大張數 [{current_max}]: ")
    if val:
        try:
            vwap["max_single_order_lots"] = int(val)
        except ValueError:
            print("  * 需為整數，保留原值")

    # --- 單筆最小張數 ---
    current_min = vwap.get("min_order_lots", 1)
    val = _safe_input(f"  單筆最小張數 [{current_min}]: ")
    if val:
        try:
            vwap["min_order_lots"] = int(val)
        except ValueError:
            print("  * 需為整數，保留原值")

    # --- 深度檔位 ---
    current_spread = vwap.get("price_spread_levels", 3)
    val = _safe_input(f"  深度檔位 (1~5) [{current_spread}]: ")
    if val:
        try:
            v = int(val)
            if 1 <= v <= 5:
                vwap["price_spread_levels"] = v
            else:
                print("  * 範圍 1~5，保留原值")
        except ValueError:
            print("  * 需為整數，保留原值")

    # --- Adaptive 開關 ---
    current_adp = adaptive.get("enabled", True)
    hint_adp = "ON" if current_adp else "OFF"
    val = _safe_input(f"  Adaptive (on/off) [{hint_adp}]: ").lower()
    if val in ("on", "true", "1"):
        adaptive["enabled"] = True
    elif val in ("off", "false", "0"):
        adaptive["enabled"] = False

    # --- ATO 結束時間 ---
    current_ato = adaptive.get("ato_end_time", "09:05:00")
    val = _safe_input(f"  ATO 結束時間 [{current_ato}]: ")
    if val:
        if re.match(r"^\d{2}:\d{2}:\d{2}$", val):
            adaptive["ato_end_time"] = val
        else:
            print("  * 格式應為 HH:MM:SS，保留原值")

    # --- 下單開始時間 ---
    current_order_start = adaptive.get("order_start_time", "09:10:00")
    val = _safe_input(f"  下單開始時間 [{current_order_start}]: ")
    if val:
        if re.match(r"^\d{2}:\d{2}:\d{2}$", val):
            adaptive["order_start_time"] = val
        else:
            print("  * 格式應為 HH:MM:SS，保留原值")

    # --- MO/LO 門檻 ---
    current_molo = adaptive.get("mo_lo_threshold", 0.5)
    val = _safe_input(f"  MO/LO 門檻 [{current_molo}]: ")
    if val:
        try:
            adaptive["mo_lo_threshold"] = float(val)
        except ValueError:
            print("  * 需為數字，保留原值")

    # --- 回望天數 ---
    current_lookback = adaptive.get("volume_lookback_days", 20)
    val = _safe_input(f"  回望天數 [{current_lookback}]: ")
    if val:
        try:
            adaptive["volume_lookback_days"] = int(val)
        except ValueError:
            print("  * 需為整數，保留原值")

    # --- 寫回 YAML ---
    with open(config_path, "w", encoding="utf-8") as f:
        yaml.dump(raw, f, allow_unicode=True, default_flow_style=False, sort_keys=False)

    print()
    print(f"  設定已儲存至 {config_path}")
    print()


# ===========================================================================
# 單股回測互動流程
# ===========================================================================


def _wizard_single(config_path: str) -> None:
    """互動式單股回測精靈。"""
    print()
    print("=== 單股回測 ===")
    print()

    latest_date = detect_latest_date()

    # Step 1: 回測日期
    date = _prompt_date(latest_date)

    # Step 2: 股票代碼
    stock_id = _prompt_stock_id(date)

    # Step 3: 目標金額
    engine = VWAPEngine(config_path)
    default_amount = engine.config.total_volume_quote
    amount = _prompt_amount(default_amount)

    # Step 4: 圖表 / CSV
    gen_chart = _confirm("產生圖表?", default_yes=True)
    gen_csv = _confirm("產生 CSV?", default_yes=True)

    # 覆蓋金額
    engine.config.override(total_volume=amount)

    # 執行回測
    print()
    print("-" * 40)
    company_name = _get_company_name(stock_id)
    print(f"  股票: {stock_id} {company_name}")
    print(f"  日期: {date}")
    print(f"  金額: {format_amount(amount)} TWD")
    print(f"  圖表: {'是' if gen_chart else '否'}  |  CSV: {'是' if gen_csv else '否'}")
    print("-" * 40)
    print()

    metrics = engine.run_single(
        stock_id=stock_id,
        date=date,
        no_csv=not gen_csv,
        no_chart=not gen_chart,
    )

    if metrics is None:
        print()
        print("[!] 回測失敗或無成交")
    else:
        print()
        print("[OK] 單股回測完成")

    print()


def _prompt_date(latest_date: Optional[str]) -> str:
    """提示使用者輸入回測日期。

    Args:
        latest_date: 自動偵測到的最新日期 (可為 None)。

    Returns:
        確認有效的日期字串。
    """
    default_hint = f" [{latest_date}]" if latest_date else ""
    while True:
        raw = _safe_input(f"回測日期 (YYYY-MM-DD){default_hint}: ")
        date = raw if raw else latest_date
        if date is None:
            print("  * 請輸入日期 (例: 2025-01-15)")
            continue
        if not re.match(r"^\d{4}-\d{2}-\d{2}$", date):
            print("  * 日期格式應為 YYYY-MM-DD")
            continue
        if not date_dir_exists(date):
            print(f"  * 日期 {date} 無資料目錄")
            continue
        return date


def _prompt_stock_id(date: str) -> str:
    """提示使用者輸入股票代碼，並驗證資料存在。

    Args:
        date: 已確認的回測日期。

    Returns:
        確認有效的股票代碼。
    """
    while True:
        stock_id = _safe_input("股票代碼: ")
        if not stock_id:
            print("  * 股票代碼為必填")
            continue

        # 查詢公司名稱
        company_name = _get_company_name(stock_id)
        if company_name != stock_id:
            print(f"  -> {company_name}")

        # 檢查資料是否存在
        if stock_data_exists(stock_id, date):
            return stock_id

        print(f"  * {stock_id} 在 {date} 無資料")
        recent = list_available_dates_for_stock(stock_id, limit=5)
        if recent:
            print(f"  最近有資料的日期: {', '.join(recent)}")
        else:
            print(f"  找不到 {stock_id} 的任何資料")


def _prompt_amount(default: float) -> float:
    """提示使用者輸入目標金額。

    Args:
        default: 預設金額 (來自 config)。

    Returns:
        目標金額。
    """
    while True:
        raw = _safe_input(f"目標金額(TWD) [{format_amount(default)}]: ")
        if not raw:
            return default
        parsed = parse_amount(raw)
        if parsed is not None:
            return parsed
        print("  * 格式無法辨識，請輸入數字或 '500萬' / '1.5億' 格式")


# ===========================================================================
# 批次回測互動流程
# ===========================================================================


def _wizard_batch(config_path: str) -> None:
    """互動式批次回測精靈。"""
    print()
    print("=== 批次回測 ===")
    print()

    latest_date = detect_latest_date()

    # Step 1: 日期
    date = _prompt_date(latest_date)

    # Step 2: 股票清單
    stock_list = _prompt_stock_list(date)

    # Step 3: 目標金額
    engine = VWAPEngine(config_path)
    default_amount = engine.config.total_volume_quote
    amount = _prompt_amount(default_amount)

    # Step 4: 圖表 / CSV
    gen_chart = _confirm("產生圖表?", default_yes=True)
    gen_csv = _confirm("產生 CSV?", default_yes=True)

    # 覆蓋金額
    engine.config.override(total_volume=amount)

    # 確認摘要
    print()
    print("-" * 40)
    print(f"  日期:     {date}")
    print(f"  股票數:   {len(stock_list)}")
    print(f"  金額/股:  {format_amount(amount)} TWD")
    print(f"  圖表: {'是' if gen_chart else '否'}  |  CSV: {'是' if gen_csv else '否'}")
    print("-" * 40)
    print()

    if not _confirm("確認開始批次回測?", default_yes=True):
        print("  已取消")
        return

    # 執行
    results = engine.run_batch(
        stock_list=stock_list,
        date=date,
        no_csv=not gen_csv,
        no_chart=not gen_chart,
    )

    # 摘要表格
    _print_batch_summary(results)
    print()


def _prompt_stock_list(date: str) -> List[str]:
    """提示使用者輸入股票清單或 'all'。

    Args:
        date: 已確認的回測日期。

    Returns:
        股票代碼清單。
    """
    available = list_stocks_for_date(date)
    print(f"  {date} 共有 {len(available)} 檔股票資料")
    print()

    while True:
        raw = _safe_input("股票代碼 (空格分隔，或 'all'): ")
        if not raw:
            print("  * 請輸入至少一個股票代碼")
            continue

        if raw.lower() == "all":
            print(f"  將回測全部 {len(available)} 檔股票")
            if not _confirm("確認?", default_yes=True):
                continue
            return available

        tokens = raw.split()
        # 驗證每一個代碼
        valid: List[str] = []
        invalid: List[str] = []
        for t in tokens:
            if stock_data_exists(t, date):
                valid.append(t)
            else:
                invalid.append(t)

        if invalid:
            print(f"  * 以下股票在 {date} 無資料: {', '.join(invalid)}")
            if not valid:
                continue
            if not _confirm(f"只回測有效的 {len(valid)} 檔?", default_yes=True):
                continue

        if valid:
            return valid


def _print_batch_summary(results: List[VWAPMetrics]) -> None:
    """印出批次回測的摘要表格。

    Args:
        results: 回測結果清單。
    """
    if not results:
        print()
        print("[!] 批次回測無任何成功結果")
        return

    print()
    print("=" * 80)
    print("  批次回測摘要")
    print("=" * 80)

    # 表頭
    header = (
        f"{'股票':>6}  {'公司':>8}  {'執行VWAP':>10}  {'市場VWAP':>10}  "
        f"{'滑價(bps)':>10}  {'完成率':>7}  {'成交金額':>14}"
    )
    print(header)
    print("-" * 80)

    for m in results:
        company = _get_company_name(m.stock_id)
        if len(company) > 4:
            company = company[:4]
        print(
            f"{m.stock_id:>6}  {company:>8}  "
            f"{m.vwap_price:>10.2f}  {m.market_vwap:>10.2f}  "
            f"{m.slippage_bps_vs_market:>10.2f}  "
            f"{m.completion_ratio:>6.1%}  "
            f"{format_amount(m.total_spent_amount):>14}"
        )

    print("-" * 80)

    # 匯總
    avg_slippage = sum(m.slippage_bps_vs_market for m in results) / len(results)
    avg_completion = sum(m.completion_ratio for m in results) / len(results)
    total_amount = sum(m.total_spent_amount for m in results)

    print(
        f"{'平均':>6}  {'':>8}  {'':>10}  {'':>10}  "
        f"{avg_slippage:>10.2f}  "
        f"{avg_completion:>6.1%}  "
        f"{format_amount(total_amount):>14}"
    )
    print("=" * 80)
    print(f"  成功: {len(results)} 檔")
    print()


# ===========================================================================
# 命令列模式 (backward compatible)
# ===========================================================================


def _run_cli() -> None:
    """原始的 argparse 命令列模式，維持向後相容。"""
    parser = argparse.ArgumentParser(
        description="VWAP 執行策略回測系統",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )

    parser.add_argument(
        "--mode",
        choices=["single", "batch"],
        default="single",
        help="執行模式: single (單股) / batch (批次)",
    )
    parser.add_argument("--stock_id", type=str, help="股票代碼 (single 模式)")
    parser.add_argument("--date", type=str, required=True, help="回測日期 (YYYY-MM-DD)")
    parser.add_argument(
        "--stock_list",
        nargs="+",
        type=str,
        help="股票代碼清單 (batch 模式)",
    )
    parser.add_argument(
        "--config",
        type=str,
        default=_DEFAULT_CONFIG_PATH,
        help="設定檔路徑 (預設: vwap_config.yaml)",
    )

    # 覆蓋設定參數
    parser.add_argument("--total_volume", type=float, help="覆蓋 total_volume_quote (TWD)")

    # 輸出控制
    parser.add_argument("--no_csv", action="store_true", help="跳過 CSV 輸出")
    parser.add_argument("--no_chart", action="store_true", help="跳過圖表產生")

    args = parser.parse_args()

    _setup_logging()

    # 初始化引擎
    engine = VWAPEngine(args.config)

    # 套用命令列覆蓋
    engine.config.override(
        total_volume=args.total_volume,
    )

    if args.mode == "single":
        if not args.stock_id:
            parser.error("single 模式需要指定 --stock_id")

        metrics = engine.run_single(
            stock_id=args.stock_id,
            date=args.date,
            no_csv=args.no_csv,
            no_chart=args.no_chart,
        )

        if metrics is None:
            logging.getLogger(__name__).error("回測失敗")
            sys.exit(1)

    elif args.mode == "batch":
        if not args.stock_list:
            parser.error("batch 模式需要指定 --stock_list")

        results = engine.run_batch(
            stock_list=args.stock_list,
            date=args.date,
            no_csv=args.no_csv,
            no_chart=args.no_chart,
        )

        if not results:
            logging.getLogger(__name__).error("批次回測無任何成功結果")
            sys.exit(1)


# ===========================================================================
# 互動式主選單
# ===========================================================================


def _run_interactive() -> None:
    """互動式主選單迴圈。"""
    _setup_logging()

    config_path = _DEFAULT_CONFIG_PATH

    while True:
        try:
            _show_main_menu()
            choice = _safe_input("請選擇 > ")

            if choice == "1":
                _wizard_single(config_path)
            elif choice == "2":
                _wizard_batch(config_path)
            elif choice == "3":
                _edit_settings(config_path)
            elif choice == "4":
                print()
                print("Bye!")
                break
            else:
                print("  * 請輸入 1~4")

        except _ReturnToMenu:
            print("  (返回主選單)")
            continue


def _show_main_menu() -> None:
    """顯示主選單。"""
    print()
    print("====================================")
    print("  Adaptive VWAP 回測系統 v2.0")
    print("====================================")
    print()
    print("  [1] 單股回測")
    print("  [2] 批次回測")
    print("  [3] 查看/修改設定")
    print("  [4] 離開")
    print()


# ===========================================================================
# 進入點
# ===========================================================================


def main() -> None:
    """主進入點：有命令列引數走 argparse，否則進入互動式選單。"""
    if len(sys.argv) > 1:
        _run_cli()
    else:
        _run_interactive()


if __name__ == "__main__":
    main()
