"""
Generate replay_data.json for the StrongestVwap replay viewer.
Bypasses Parquet.Net by doing all parquet reading in Python (polars/pandas).

Usage:
    python generate_replay_data.py                                   # Interactive: pick date
    python generate_replay_data.py --date 2026-03-06                 # Specific date
    python generate_replay_data.py --date 2026-03-06 --output D:\\out # Custom output dir
    python generate_replay_data.py --interval 1                      # 1s precision (large)
    python generate_replay_data.py --list                            # List available dates
    python generate_replay_data.py --last 5                          # Last 5 dates
    python generate_replay_data.py --serve                           # Generate + open browser
"""

import argparse
import json
import os
import shutil
import subprocess
import sys
import time

import polars as pl
import pandas as pd

# ─── Config defaults (match ReplayEngine.cs) ───
CONFIG = {
    "member_min_month_trading_val": 200_000_000,
    "group_min_month_trading_val": 3_000_000_000,
    "group_min_avg_pct_chg": 0.01,
    "group_min_val_ratio": 0.1,
    "is_weighted_avg": False,
    "group_valid_top_n": 20,
    "top_group_rank_threshold": 10,
    "top_group_max_select": 1,
    "normal_group_max_select": 1,
    "entry_min_vwap_pct_chg": 0.00,
}

# ─── Paths ───
SCREENING_CSV = r"C:\Users\User\Documents\_02_bt\Backtest_tick_module\screening_results.csv"
CLOSE_PARQUET = r"D:\03_預估量相關資量\CSharp\close.parquet"
TICK_BASE = r"D:\feature_data\feature"
DEFAULT_OUTPUT = r"D:\03_預估量相關資量\CSharp\StrongestVwap\output\replay"


def load_screening(csv_path: str, target_date: str) -> pd.DataFrame:
    df = pd.read_csv(csv_path)
    df = df[df["date"] == target_date].copy()
    df["stock_id"] = df["stock_id"].astype(str)
    print(f"[REPLAY] Loaded {len(df)} screening records for {target_date}")
    print(f"[REPLAY] Groups: {df['category'].nunique()}, Stocks: {df['stock_id'].nunique()}")
    return df


def load_prev_close(parquet_path: str, target_date: str, stock_ids: list[str]) -> dict[str, float]:
    df = pd.read_parquet(parquet_path)

    date_col = None
    for col in df.columns:
        if col.lower() in ("date", "__index_level_0__", "index"):
            date_col = col
            break

    if date_col is None and df.index.name and df.index.name.lower() in ("date", "__index_level_0__"):
        df = df.reset_index()
        date_col = df.columns[0]

    if date_col is None:
        date_col = df.columns[-1]

    df[date_col] = pd.to_datetime(df[date_col])
    target_dt = pd.Timestamp(target_date)

    dates_before = df[df[date_col] < target_dt][date_col].sort_values()
    if len(dates_before) == 0:
        print(f"[REPLAY] No previous trading date found before {target_date}")
        return {}

    prev_date = dates_before.iloc[-1]
    prev_row = df[df[date_col] == prev_date].iloc[0]

    result = {}
    for sid in stock_ids:
        if sid in prev_row.index:
            val = prev_row[sid]
            if pd.notna(val) and float(val) > 0:
                result[sid] = float(val)

    print(f"[REPLAY] Prev close from {prev_date.strftime('%Y-%m-%d')}: {len(result)}/{len(stock_ids)} stocks")
    return result


def load_tick_data(tick_base: str, date: str, stock_ids: list[str]) -> pl.DataFrame:
    all_frames = []
    loaded = 0
    skipped = 0

    for sid in stock_ids:
        tick_path = os.path.join(tick_base, date, f"{sid}.parquet")
        if not os.path.exists(tick_path):
            skipped += 1
            continue

        try:
            df = pl.read_parquet(tick_path)

            if "type" in df.columns:
                df = df.filter(pl.col("type") == "Trade")

            if "time" not in df.columns or "price" not in df.columns:
                skipped += 1
                continue

            cols = ["time", "price"]
            for c in ("volume", "vwap", "day_high"):
                if c in df.columns:
                    cols.append(c)

            df = df.select(cols)
            df = df.with_columns(pl.lit(sid).alias("stock_id"))

            df = df.filter(
                (pl.col("time").dt.hour() >= 9)
                & (
                    (pl.col("time").dt.hour() < 13)
                    | (
                        (pl.col("time").dt.hour() == 13)
                        & (pl.col("time").dt.minute() <= 30)
                    )
                )
            )

            if len(df) > 0:
                all_frames.append(df)
                loaded += 1
            else:
                skipped += 1

        except Exception as e:
            print(f"[REPLAY] Error loading {sid}: {e}")
            skipped += 1

    print(f"[REPLAY] Tick data: {loaded} stocks loaded, {skipped} skipped")

    if not all_frames:
        return pl.DataFrame()

    merged = pl.concat(all_frames, how="diagonal")
    merged = merged.sort("time")
    print(f"[REPLAY] Total merged ticks: {len(merged)}")
    return merged


def _price_change_pct(state: dict) -> float:
    pc = state["prev_close"]
    if pc > 0:
        return (state["last_price"] - pc) / pc
    return 0.0


def _vwap_change_pct(state: dict) -> float:
    pc = state["prev_close"]
    vwap = state["vwap"]
    if pc > 0 and vwap > 0:
        return (vwap - pc) / pc
    return 0.0


def create_snapshot(
    total_seconds: int,
    stock_info: dict[str, dict],
    group_members: dict[str, list[str]],
    group_val_ratios: dict[str, float],
    config: dict,
) -> dict:
    valid_groups = []

    for group_name, member_ids in group_members.items():
        valid_members = []
        for sid in member_ids:
            if sid not in stock_info:
                continue
            s = stock_info[sid]
            if s["avg_amount_20d"] < config["member_min_month_trading_val"]:
                continue
            if s["last_price"] <= 0:
                continue
            valid_members.append(s)

        if not valid_members:
            continue

        total_monthly_val = sum(m["avg_amount_20d"] for m in valid_members)

        if config["is_weighted_avg"]:
            # Match C# StrongGroupScreener: weight by MonthlyAvgTradingValue (avg_amount_20d)
            sum_pct = sum(
                _price_change_pct(m) * m["avg_amount_20d"] for m in valid_members
            )
            sum_weight = sum(m["avg_amount_20d"] for m in valid_members)
            avg_pct = sum_pct / sum_weight if sum_weight > 0 else 0
        else:
            sum_pct = sum(_price_change_pct(m) for m in valid_members)
            avg_pct = sum_pct / len(valid_members) if valid_members else 0

        val_ratio = group_val_ratios.get(group_name, 0)

        # Check structural validity (月均成交值 + 成交值比), but NOT avg price change
        is_valid = (
            total_monthly_val >= config["group_min_month_trading_val"]
            and val_ratio >= config["group_min_val_ratio"]
        )

        if not is_valid:
            continue

        member_snapshots = []
        for m in valid_members:
            member_snapshots.append(
                {
                    "stockId": m["stock_id"],
                    "stockName": m["stock_name"],
                    "priceChangePct": round(_price_change_pct(m), 4),
                    "vwapChangePct": round(_vwap_change_pct(m), 4),
                    "lastPrice": round(m["last_price"], 2),
                    "prevClose": round(m["prev_close"], 2),
                    "vwap": round(m["vwap"], 2),
                    "rankInGroup": 0,
                    "isSelected": False,
                    "isDisposal": m.get("is_disposal", False),
                    "avgAmount20d": m.get("avg_amount_20d", 0),
                }
            )

        valid_groups.append(
            {
                "groupName": group_name,
                "rank": 0,
                "avgPriceChangePct": round(avg_pct, 4),
                "valRatio": round(val_ratio, 2),
                "totalMonthlyVal": round(total_monthly_val, 0),
                "validMemberCount": len(valid_members),
                "isValid": True,
                "members": member_snapshots,
            }
        )

    valid_groups.sort(key=lambda g: g["avgPriceChangePct"], reverse=True)
    for i, g in enumerate(valid_groups):
        g["rank"] = i + 1

    selected_stock_ids = set()
    for g in valid_groups:
        if g["rank"] > config["group_valid_top_n"]:
            continue

        # Match C# StrongGroupScreener: rank members by VwapChangePct
        g["members"].sort(key=lambda m: m["vwapChangePct"], reverse=True)
        for j, m in enumerate(g["members"]):
            m["rankInGroup"] = j + 1

        max_select = (
            config["top_group_max_select"]
            if g["rank"] <= config["top_group_rank_threshold"]
            else config["normal_group_max_select"]
        )

        for m in g["members"]:
            if (
                m["rankInGroup"] <= max_select
                and m["vwapChangePct"] >= config["entry_min_vwap_pct_chg"]
            ):
                m["isSelected"] = True
                selected_stock_ids.add(m["stockId"])

    hours = 9 + total_seconds // 3600
    minutes = (total_seconds % 3600) // 60
    seconds = total_seconds % 60
    time_str = f"{hours:02d}:{minutes:02d}:{seconds:02d}"

    return {
        "time": time_str,
        "totalSeconds": total_seconds,
        "groups": valid_groups,
        "validGroupCount": len(valid_groups),
        "totalSelectedStocks": len(selected_stock_ids),
    }


def process_ticks(
    merged_ticks: pl.DataFrame,
    screening_df: pd.DataFrame,
    prev_closes: dict[str, float],
    config: dict,
    sample_interval: int = 5,
) -> list[dict]:
    group_members: dict[str, list[str]] = {}
    group_val_ratios: dict[str, float] = {}
    stock_info: dict[str, dict] = {}

    for _, row in screening_df.iterrows():
        sid = str(row["stock_id"])
        cat = row["category"]

        if cat not in group_members:
            group_members[cat] = []
        if sid not in group_members[cat]:
            group_members[cat].append(sid)

        group_val_ratios[cat] = row.get("val_ratio", 0)

        if sid not in stock_info:
            stock_info[sid] = {
                "stock_id": sid,
                "stock_name": row.get("stock_name", ""),
                "category": cat,
                "avg_amount_20d": row.get("avg_amount_20d", 0),
                "prev_close": prev_closes.get(sid, 0),
                "last_price": 0.0,
                "vwap": 0.0,
                "day_high": 0.0,
                "cumulative_volume": 0.0,
                "cumulative_value": 0.0,
                "is_disposal": bool(row.get("is_disposal", False)),
            }

    times = merged_ticks["time"].to_list()
    prices = merged_ticks["price"].to_list()
    stock_ids_col = merged_ticks["stock_id"].to_list()
    volumes = (
        merged_ticks["volume"].to_list()
        if "volume" in merged_ticks.columns
        else [0.0] * len(times)
    )
    vwaps = (
        merged_ticks["vwap"].to_list()
        if "vwap" in merged_ticks.columns
        else [0.0] * len(times)
    )
    day_highs = (
        merged_ticks["day_high"].to_list()
        if "day_high" in merged_ticks.columns
        else [0.0] * len(times)
    )

    market_open_seconds = 9 * 3600
    last_snapshot_second = -sample_interval  # ensure first tick creates a snapshot
    snapshots = []

    total_ticks = len(times)
    report_interval = max(total_ticks // 10, 1)

    for i in range(total_ticks):
        t = times[i]
        sid = stock_ids_col[i]
        price = float(prices[i])
        vol = float(volumes[i])
        vwap_val = float(vwaps[i])
        dh = float(day_highs[i])

        if sid in stock_info:
            s = stock_info[sid]
            s["last_price"] = price
            if vwap_val > 0:
                s["vwap"] = vwap_val
            if dh > 0:
                s["day_high"] = dh
            s["cumulative_volume"] += vol
            s["cumulative_value"] += price * vol

        current_second = t.hour * 3600 + t.minute * 60 + t.second - market_open_seconds
        if current_second < 0:
            current_second = 0

        # Sample at interval boundaries
        if current_second >= last_snapshot_second + sample_interval:
            # Align to interval
            aligned = (current_second // sample_interval) * sample_interval
            snapshot = create_snapshot(
                aligned, stock_info, group_members, group_val_ratios, config
            )
            snapshots.append(snapshot)
            last_snapshot_second = aligned

        if (i + 1) % report_interval == 0:
            pct = (i + 1) / total_ticks * 100
            print(f"[REPLAY] Processing: {pct:.0f}% ({i+1}/{total_ticks})", end="\r")

    # Always include the last state as a final snapshot
    if times:
        t = times[-1]
        final_second = t.hour * 3600 + t.minute * 60 + t.second - market_open_seconds
        if final_second > last_snapshot_second:
            snapshot = create_snapshot(
                final_second, stock_info, group_members, group_val_ratios, config
            )
            snapshots.append(snapshot)

    print(f"\n[REPLAY] Generated {len(snapshots)} time snapshots (interval={sample_interval}s)")
    return snapshots


def get_available_dates(csv_path: str) -> list[str]:
    """Get all available dates from screening CSV."""
    df = pd.read_csv(csv_path)
    return sorted(df["date"].unique().tolist())


def interactive_pick_date(dates: list[str]) -> str:
    """Interactive date picker: show recent dates, let user choose."""
    recent = dates[-20:]
    print("\n Available dates (last 20):")
    print("-" * 40)
    for i, d in enumerate(recent):
        marker = " <-- latest" if i == len(recent) - 1 else ""
        print(f"  [{i + 1:2d}] {d}{marker}")
    print("-" * 40)
    print(f"  Total available: {len(dates)} dates ({dates[0]} ~ {dates[-1]})")
    print()

    while True:
        ans = input("Enter number or date (YYYY-MM-DD), [Enter]=latest: ").strip()
        if ans == "":
            return recent[-1]
        if ans in dates:
            return ans
        try:
            idx = int(ans)
            if 1 <= idx <= len(recent):
                return recent[idx - 1]
        except ValueError:
            pass
        print(f"  Invalid input: '{ans}'. Try again.")


def print_group_summary(snapshots: list[dict], date: str):
    """Print group ranking summary using the last snapshot."""
    if not snapshots:
        return

    last = snapshots[-1]
    groups = last.get("groups", [])

    print(f"\n{'='*80}")
    print(f" Group Summary @ {last['time']}  ({date})")
    print(f" Total groups: {len(groups)}, Selected stocks: {last['totalSelectedStocks']}")
    print(f"{'='*80}")
    print(
        f"{'Rank':>4}  {'Group':<16} {'Avg Chg%':>9} {'ValRatio':>9} "
        f"{'MonthlyVal':>14} {'Members':>7}  Selected"
    )
    print("-" * 80)

    for g in groups:
        avg_pct_str = f"{g['avgPriceChangePct'] * 100:+.2f}%"
        monthly_val = g["totalMonthlyVal"]
        if monthly_val >= 1e8:
            mv_str = f"{monthly_val / 1e8:.1f}億"
        else:
            mv_str = f"{monthly_val / 1e4:.0f}萬"

        selected = [m for m in g["members"] if m["isSelected"]]
        sel_str = (
            ", ".join(
                f"{m['stockId']}({m['stockName']}) vwap:{m.get('vwapChangePct',0)*100:+.1f}%"
                for m in selected
            )
            if selected
            else "-"
        )

        print(
            f"{g['rank']:>4}  {g['groupName']:<16} {avg_pct_str:>9} "
            f"{g['valRatio']:>9.2f} {mv_str:>14} {g['validMemberCount']:>7}  {sel_str}"
        )

    # Member detail for top groups
    print(f"\n{'─'*80}")
    print(f" Member Details (all groups)")
    print(f"{'─'*80}")

    for g in groups:
        print(f"\n  [{g['rank']}] {g['groupName']} (avg price chg: {g['avgPriceChangePct']*100:+.2f}%)")
        print(f"      {'#':>2}  {'Code':<6} {'Name':<10} {'VWAP Chg%':>9} {'Price Chg%':>10}  {'Price':>8}  {'VWAP':>8}  {'Prev':>8}")
        # Members already sorted by vwapChangePct (rankInGroup order)
        for m in g["members"]:
            sel = " ★" if m["isSelected"] else ""
            vwap_pct = m.get("vwapChangePct", 0)
            vwap_val = m.get("vwap", 0)
            print(
                f"      {m['rankInGroup']:>2}. {m['stockId']:<6} {m['stockName']:<10} "
                f"{vwap_pct*100:>+8.2f}% {m['priceChangePct']*100:>+9.2f}%  "
                f"{m['lastPrice']:>8.2f}  {vwap_val:>8.2f}  {m['prevClose']:>8.2f}{sel}"
            )

    print()


def generate_replay(
    target_date: str,
    output_dir: str,
    screening_csv: str,
    close_parquet: str,
    tick_base: str,
    interval: int,
) -> str | None:
    """Core generation logic. Returns output path on success, None on failure."""
    t0 = time.time()
    print(f"\n{'='*60}")
    print(f"[REPLAY] Generating replay data for {target_date}")
    print(f"[REPLAY] Snapshot interval: {interval}s")
    print(f"{'='*60}")

    # 1. Load screening
    screening_df = load_screening(screening_csv, target_date)
    if len(screening_df) == 0:
        print("[REPLAY] No screening records. Aborting.")
        return None

    stock_ids = screening_df["stock_id"].unique().tolist()

    # 2. Load prev close
    prev_closes = load_prev_close(close_parquet, target_date, stock_ids)

    # 3. Load tick data
    merged_ticks = load_tick_data(tick_base, target_date, stock_ids)
    if len(merged_ticks) == 0:
        print("[REPLAY] No tick data. Aborting.")
        return None

    # 4. Process ticks
    snapshots = process_ticks(
        merged_ticks, screening_df, prev_closes, CONFIG, interval
    )

    # 5. Print group summary (using last snapshot)
    print_group_summary(snapshots, target_date)

    # 6. Build result
    result = {
        "date": target_date,
        "config": CONFIG,
        "snapshots": snapshots,
    }

    # 7. Export JSON
    os.makedirs(output_dir, exist_ok=True)
    output_path = os.path.join(output_dir, "replay_data.json")
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, separators=(",", ":"))

    # 8. Copy replay.html to output dir
    html_src = os.path.join(
        os.path.dirname(os.path.abspath(__file__)),
        "StrongestVwap", "Web", "replay.html",
    )
    if os.path.exists(html_src):
        html_dst = os.path.join(output_dir, "replay.html")
        shutil.copy2(html_src, html_dst)

    elapsed = time.time() - t0
    file_size_mb = os.path.getsize(output_path) / 1024 / 1024
    print(f"\n[REPLAY] Output: {output_path}")
    print(f"[REPLAY] File size: {file_size_mb:.1f} MB")
    print(f"[REPLAY] Snapshots: {len(snapshots)}")
    print(f"[REPLAY] Elapsed: {elapsed:.1f}s")
    print(f"{'='*60}")
    return output_path


def serve_and_open(output_dir: str, port: int = 8080):
    """Start a local HTTP server and open the browser."""
    import webbrowser
    from http.server import HTTPServer, SimpleHTTPRequestHandler

    os.chdir(output_dir)
    url = f"http://localhost:{port}/replay.html"
    print(f"\n[SERVE] Starting server at {url}")
    print(f"[SERVE] Press Ctrl+C to stop\n")
    webbrowser.open(url)

    handler = SimpleHTTPRequestHandler
    handler.log_message = lambda *a: None  # suppress log noise
    server = HTTPServer(("", port), handler)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\n[SERVE] Server stopped.")
        server.server_close()


def main():
    parser = argparse.ArgumentParser(
        description="Replay data generator for StrongestVwap group screening",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""\
examples:
  %(prog)s                          Interactive date picker
  %(prog)s --date 2026-03-06        Specific date
  %(prog)s --date 2026-03-06 --serve  Generate + open browser
  %(prog)s --list                   List all available dates
  %(prog)s --last 5                 Generate last 5 dates
""",
    )
    parser.add_argument("--date", default=None, help="Target date (YYYY-MM-DD)")
    parser.add_argument("--output", default=DEFAULT_OUTPUT, help="Output directory")
    parser.add_argument("--screening_csv", default=SCREENING_CSV)
    parser.add_argument("--close_parquet", default=CLOSE_PARQUET)
    parser.add_argument("--tick_base", default=TICK_BASE)
    parser.add_argument(
        "--interval", type=int, default=5,
        help="Snapshot interval in seconds (default: 5)",
    )
    parser.add_argument(
        "--list", action="store_true", dest="list_dates",
        help="List all available dates and exit",
    )
    parser.add_argument(
        "--last", type=int, default=None, metavar="N",
        help="Generate replay for last N dates (batch)",
    )
    parser.add_argument(
        "--serve", action="store_true",
        help="Start local HTTP server and open browser after generation",
    )
    parser.add_argument(
        "--port", type=int, default=8080,
        help="HTTP server port (default: 8080)",
    )
    args = parser.parse_args()

    # ── List mode ──
    if args.list_dates:
        dates = get_available_dates(args.screening_csv)
        print(f"\nAvailable dates ({len(dates)} total):")
        print(f"Range: {dates[0]} ~ {dates[-1]}\n")
        for i, d in enumerate(dates):
            print(f"  {d}", end="  " if (i + 1) % 5 != 0 else "\n")
        print()
        return

    # ── Batch mode (--last N) ──
    if args.last is not None:
        dates = get_available_dates(args.screening_csv)
        batch_dates = dates[-args.last :]
        print(f"\n[BATCH] Generating {len(batch_dates)} dates: {batch_dates[0]} ~ {batch_dates[-1]}")
        for d in batch_dates:
            out_dir = os.path.join(args.output, d)
            generate_replay(d, out_dir, args.screening_csv, args.close_parquet, args.tick_base, args.interval)
        print(f"\n[BATCH] Done. {len(batch_dates)} dates generated under {args.output}")
        return

    # ── Single date mode ──
    target_date = args.date
    if target_date is None:
        # Interactive: pick date
        dates = get_available_dates(args.screening_csv)
        if not dates:
            print("[ERROR] No dates found in screening CSV.")
            return
        target_date = interactive_pick_date(dates)

    output_path = generate_replay(
        target_date, args.output, args.screening_csv,
        args.close_parquet, args.tick_base, args.interval,
    )

    if output_path and args.serve:
        serve_and_open(args.output, args.port)


if __name__ == "__main__":
    main()
