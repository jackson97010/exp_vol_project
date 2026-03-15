#!/bin/bash
# run_ma5_bias5.sh
# MA5 bias threshold backtest: screening_results.csv x 263 dates
# Config: Bo_v2_modeC_screening.yaml + --ma5_bias5 flag
# Output: D:\C#_backtest\MA5_bias5

PROJECT_DIR="D:/03_預估量相關資量/CSharp/BacktestModule"
OUTPUT_PATH="D:/C#_backtest/MA5_bias5"
CONFIG_FILE="$PROJECT_DIR/Bo_v2_modeC_screening.yaml"
SCREENING_FILE="$PROJECT_DIR/screening_results.csv"

# Get unique dates
DATES=$(tail -n +2 "$SCREENING_FILE" | cut -d',' -f1 | sort -u)
TOTAL=$(echo "$DATES" | wc -l)
IDX=0
START_TIME=$(date +%s)

echo "============================================"
echo "MA5 Bias5 Threshold Backtest"
echo "Config: Bo_v2_modeC_screening.yaml + --ma5_bias5"
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
        --ma5_bias5 \
        --no_chart \
        --config "$CONFIG_FILE" \
        --output_path "$OUTPUT_PATH" 2>&1 | grep -E "(Batch backtest|ERROR.*Failed|MA5 bias CSV)"
done

TOTAL_TIME=$(( ($(date +%s) - START_TIME) / 60 ))
echo ""
echo "============================================"
echo "Backtest Complete!"
echo "Total time: ${TOTAL_TIME} minutes"
echo "Output: $OUTPUT_PATH"
echo "============================================"
