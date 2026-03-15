#!/bin/bash
# ModeC_screening with strategy_b_stop_loss_ticks_small=4

PROJECT_DIR="D:/03_預估量相關資量/CSharp/BacktestModule"
SCREENING_FILE="$PROJECT_DIR/screening_results.csv"
CONFIG_FILE="$PROJECT_DIR/Bo_v2_modeC_screening_sl4.yaml"
OUTPUT_PATH="D:/C#_backtest/ModeC_screening_sl4"

DATES=$(tail -n +2 "$SCREENING_FILE" | cut -d',' -f1 | sort -u)
TOTAL=$(echo "$DATES" | wc -l)
IDX=0
START_TIME=$(date +%s)

echo "============================================"
echo "ModeC_screening stop_loss_ticks_small=4"
echo "Config: Bo_v2_modeC_screening_sl4.yaml"
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
        --no_chart \
        --config "$CONFIG_FILE" \
        --output_path "$OUTPUT_PATH" 2>&1 | grep -E "(Batch backtest|ERROR.*Failed)"
done

TOTAL_TIME=$(( ($(date +%s) - START_TIME) / 60 ))
echo ""
echo "============================================"
echo "DONE! Total time: ${TOTAL_TIME} minutes"
echo "============================================"
