#!/bin/bash
# run_all_backtests.sh
# 15 backtest combinations: 5 screening files x 3 bias versions (none/bias5/bias7)
# split_entry disabled in all configs

PROJECT_DIR="D:/03_預估量相關資量/CSharp/BacktestModule"
START_ALL=$(date +%s)

run_backtest() {
    local LABEL="$1"
    local SCREENING_FILE="$2"
    local CONFIG_FILE="$3"
    local OUTPUT_PATH="$4"
    local BIAS_FLAG="$5"  # "" or "--ma5_bias5" or "--ma5_bias7"

    DATES=$(tail -n +2 "$SCREENING_FILE" | cut -d',' -f1 | sort -u)
    TOTAL=$(echo "$DATES" | wc -l)
    IDX=0
    START_TIME=$(date +%s)

    echo ""
    echo "============================================"
    echo "$LABEL"
    echo "Config: $(basename $CONFIG_FILE) $BIAS_FLAG"
    echo "Screening: $(basename $SCREENING_FILE)"
    echo "Output: $OUTPUT_PATH"
    echo "Total dates: $TOTAL"
    echo "Start: $(date)"
    echo "============================================"

    for DATE in $DATES; do
        IDX=$((IDX + 1))
        ELAPSED=$(($(date +%s) - START_TIME))
        if [ $IDX -gt 1 ]; then
            AVG=$((ELAPSED / (IDX - 1)))
            ETA=$(( AVG * (TOTAL - IDX + 1) / 60 ))
            echo "[$IDX/$TOTAL] Date: $DATE | ETA: ${ETA}min"
        else
            echo "[$IDX/$TOTAL] Date: $DATE"
        fi

        dotnet run --project "$PROJECT_DIR" -- \
            --mode batch \
            --date "$DATE" \
            --use_screening \
            --screening_file "$SCREENING_FILE" \
            --use_dynamic_liquidity \
            $BIAS_FLAG \
            --no_chart \
            --config "$CONFIG_FILE" \
            --output_path "$OUTPUT_PATH" 2>&1 | grep -E "(Batch backtest|ERROR.*Failed|MA5 bias CSV)"
    done

    TOTAL_TIME=$(( ($(date +%s) - START_TIME) / 60 ))
    echo "=> $LABEL done in ${TOTAL_TIME} minutes"
}

# ============================================================
# 1. screen_limit_up_low_amount_entry (263 dates)
# ============================================================
SF="$PROJECT_DIR/screen_limit_up_low_amount_entry.csv"
CF="$PROJECT_DIR/Bo_v2_modeC_limit_up_low_amount_entry.yaml"

run_backtest "1/15 limit_up_low_amount_entry (no bias)" \
    "$SF" "$CF" "D:/C#_backtest/limit_up_low_amount_entry" ""

run_backtest "2/15 limit_up_low_amount_entry_bias5" \
    "$SF" "$CF" "D:/C#_backtest/limit_up_low_amount_entry_bias5" "--ma5_bias5"

run_backtest "3/15 limit_up_low_amount_entry_bias7" \
    "$SF" "$CF" "D:/C#_backtest/limit_up_low_amount_entry_bias7" "--ma5_bias7"

# ============================================================
# 2. screen_limit_up_open_entry (185 dates)
# ============================================================
SF="$PROJECT_DIR/screen_limit_up_open_entry.csv"
CF="$PROJECT_DIR/Bo_v2_modeC_limit_up_open_entry.yaml"

run_backtest "4/15 limit_up_open_entry (no bias)" \
    "$SF" "$CF" "D:/C#_backtest/limit_up_open_entry" ""

run_backtest "5/15 limit_up_open_entry_bias5" \
    "$SF" "$CF" "D:/C#_backtest/limit_up_open_entry_bias5" "--ma5_bias5"

run_backtest "6/15 limit_up_open_entry_bias7" \
    "$SF" "$CF" "D:/C#_backtest/limit_up_open_entry_bias7" "--ma5_bias7"

# ============================================================
# 3. screen_limit_up_v2 (231 dates) - uses limit_up_entry config
# ============================================================
SF="$PROJECT_DIR/screen_limit_up_v2.csv"
CF="$PROJECT_DIR/Bo_v2_modeC_limit_up_entry.yaml"

run_backtest "7/15 limit_up_entry (no bias, v2 screening)" \
    "$SF" "$CF" "D:/C#_backtest/limit_up_entry" ""

run_backtest "8/15 limit_up_entry_bias5 (v2 screening)" \
    "$SF" "$CF" "D:/C#_backtest/limit_up_entry_bias5" "--ma5_bias5"

run_backtest "9/15 limit_up_entry_bias7 (v2 screening)" \
    "$SF" "$CF" "D:/C#_backtest/limit_up_entry_bias7" "--ma5_bias7"

# ============================================================
# 4. screening_results (263 dates)
# ============================================================
SF="$PROJECT_DIR/screening_results.csv"
CF="$PROJECT_DIR/Bo_v2_modeC_screening.yaml"

run_backtest "10/15 ModeC_screening (no bias)" \
    "$SF" "$CF" "D:/C#_backtest/ModeC_screening" ""

run_backtest "11/15 MA5_bias5" \
    "$SF" "$CF" "D:/C#_backtest/MA5_bias5" "--ma5_bias5"

run_backtest "12/15 MA5_bias7" \
    "$SF" "$CF" "D:/C#_backtest/MA5_bias7" "--ma5_bias7"

# ============================================================
# 5. screen_limit_up_entry (231 dates)
# ============================================================
SF="$PROJECT_DIR/screen_limit_up_entry.csv"
CF="$PROJECT_DIR/Bo_v2_modeC_limit_up_entry.yaml"

run_backtest "13/15 limit_up (no bias)" \
    "$SF" "$CF" "D:/C#_backtest/limit_up" ""

run_backtest "14/15 limit_up_bias5" \
    "$SF" "$CF" "D:/C#_backtest/limit_up_bias5" "--ma5_bias5"

run_backtest "15/15 limit_up_bias7" \
    "$SF" "$CF" "D:/C#_backtest/limit_up_bias7" "--ma5_bias7"

# ============================================================
# Summary
# ============================================================
TOTAL_ALL=$(( ($(date +%s) - START_ALL) / 60 ))
echo ""
echo "============================================"
echo "ALL 15 BACKTESTS COMPLETE!"
echo "Total time: ${TOTAL_ALL} minutes"
echo "============================================"
