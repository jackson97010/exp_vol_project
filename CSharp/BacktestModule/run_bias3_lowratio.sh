#!/bin/bash
# run_bias3_lowratio.sh
# 2 backtest groups:
# A) screen_limit_up_entry.csv + bias3 -> limit_up_bias3
# B) screening_results.csv + low_ratio -> ModeC_screening_low_ratio

PROJECT_DIR="D:/03_預估量相關資量/CSharp/BacktestModule"
START_ALL=$(date +%s)

run_backtest() {
    local LABEL="$1"
    local SCREENING_FILE="$2"
    local CONFIG_FILE="$3"
    local OUTPUT_PATH="$4"
    local EXTRA_FLAGS="$5"

    DATES=$(tail -n +2 "$SCREENING_FILE" | cut -d',' -f1 | sort -u)
    TOTAL=$(echo "$DATES" | wc -l)
    IDX=0
    START_TIME=$(date +%s)

    echo ""
    echo "============================================"
    echo "$LABEL"
    echo "Config: $(basename $CONFIG_FILE) $EXTRA_FLAGS"
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
            $EXTRA_FLAGS \
            --no_chart \
            --config "$CONFIG_FILE" \
            --output_path "$OUTPUT_PATH" 2>&1 | grep -E "(Batch backtest|ERROR.*Failed|MA5 bias CSV)"
    done

    TOTAL_TIME=$(( ($(date +%s) - START_TIME) / 60 ))
    echo "=> $LABEL done in ${TOTAL_TIME} minutes"
}

# A) limit_up_bias3
run_backtest "1/2 limit_up_bias3" \
    "$PROJECT_DIR/screen_limit_up_entry.csv" \
    "$PROJECT_DIR/Bo_v2_modeC_limit_up_entry.yaml" \
    "D:/C#_backtest/limit_up_bias3" \
    "--ma5_bias3"

# B) ModeC_screening_low_ratio
run_backtest "2/2 ModeC_screening_low_ratio" \
    "$PROJECT_DIR/screening_results.csv" \
    "$PROJECT_DIR/Bo_v2_modeC_screening.yaml" \
    "D:/C#_backtest/ModeC_screening_low_ratio" \
    "--low_ratio"

TOTAL_ALL=$(( ($(date +%s) - START_ALL) / 60 ))
echo ""
echo "============================================"
echo "ALL BACKTESTS COMPLETE!"
echo "Total time: ${TOTAL_ALL} minutes"
echo "============================================"
