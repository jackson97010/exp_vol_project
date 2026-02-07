"""
BO Reentry 策略回測程式 - 模組化版本
實現 Day High 突破進場策略，包含以下特點：
1. 漲幅上限 8.5% 限制
2. 動能衰竭出場機制
3. 重複進場邏輯：價格創新高 + 3秒外盤金額比較
4. 詳細的進場訊號記錄，顯示每個條件的檢查結果

此為重構後的精簡版入口點，實際邏輯已模組化
"""
import os
import sys
import logging
import argparse
from typing import List

# 確保模組路徑正確
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

# 匯入核心引擎
from core.backtest_engine import BacktestEngine
from core.constants import *

# 設定日誌
logging.basicConfig(
    level=logging.INFO,
    format=LOG_FORMAT,
    datefmt=LOG_DATE_FORMAT
)
logger = logging.getLogger(__name__)


def load_screening_results(date: str) -> List[str]:
    """
    載入篩選結果

    Args:
        date: 日期

    Returns:
        股票代碼列表
    """
    try:
        import pandas as pd
        df = pd.read_csv(SCREENING_RESULTS_PATH)

        # 過濾指定日期的股票
        if 'date' in df.columns:
            df = df[df['date'] == date]

        # 取得股票代碼列表
        if 'stock_id' in df.columns:
            stock_list = df['stock_id'].astype(str).tolist()
        elif 'code' in df.columns:
            stock_list = df['code'].astype(str).tolist()
        else:
            logger.warning("找不到股票代碼欄位")
            stock_list = []

        logger.info(f"載入 {len(stock_list)} 檔股票")
        return stock_list

    except FileNotFoundError:
        logger.error(f"找不到篩選結果檔案: {SCREENING_RESULTS_PATH}")
        return []
    except Exception as e:
        logger.error(f"載入篩選結果時發生錯誤: {e}")
        return []


def main():
    """主程式入口"""
    parser = argparse.ArgumentParser(description='BO Reentry 策略回測程式')

    # 模式選擇
    parser.add_argument('--mode', type=str, choices=['single', 'batch'], default='single',
                       help='執行模式: single(單一股票) 或 batch(批次回測)')

    # 單一股票回測參數
    parser.add_argument('--stock_id', type=str, help='股票代碼')
    parser.add_argument('--date', type=str, required=True, help='回測日期 (YYYY-MM-DD)')

    # 批次回測參數
    parser.add_argument('--stock_list', type=str, nargs='+', help='股票代碼列表')
    parser.add_argument('--use_screening', action='store_true', help='使用篩選結果檔案')

    # 輸出選項
    parser.add_argument('--no_csv', action='store_true', help='不輸出CSV檔案')
    parser.add_argument('--no_chart', action='store_true', help='不產生圖表')

    # 設定檔
    parser.add_argument('--config', type=str, default=DEFAULT_CONFIG_PATH,
                       help='策略設定檔路徑')

    # 參數覆蓋選項
    parser.add_argument('--entry_start_time', type=str, help='進場開始時間 (HH:MM:SS)，覆蓋配置文件中的設定')
    parser.add_argument('--liquidity_multiplier', type=float, help='流動性門檻係數，覆蓋配置文件中的設定')

    args = parser.parse_args()

    # 建立回測引擎
    engine = BacktestEngine(config_path=args.config)

    # 覆蓋配置參數（如果提供）
    if args.entry_start_time:
        from datetime import datetime
        try:
            entry_time = datetime.strptime(args.entry_start_time, '%H:%M:%S').time()
            engine.config._config['entry_start_time'] = entry_time
            engine.entry_config['entry_start_time'] = entry_time
            logger.info(f"✓ 覆蓋進場開始時間: {args.entry_start_time}")
        except ValueError:
            logger.error(f"❌ 無效的時間格式: {args.entry_start_time}，請使用 HH:MM:SS 格式")
            return

    if args.liquidity_multiplier is not None:
        engine.config._config['dynamic_liquidity_multiplier'] = args.liquidity_multiplier
        engine.entry_config['dynamic_liquidity_multiplier'] = args.liquidity_multiplier
        # 更新 EntryChecker 的配置
        engine.entry_checker.config['dynamic_liquidity_multiplier'] = args.liquidity_multiplier
        logger.info(f"✓ 覆蓋流動性門檻係數: {args.liquidity_multiplier}")

    if args.mode == 'single':
        # 單一股票回測模式
        if not args.stock_id:
            logger.error("單一股票模式需要指定 --stock_id")
            return

        logger.info(f"\n{'='*60}")
        logger.info(f"執行單一股票回測")
        logger.info(f"股票: {args.stock_id}, 日期: {args.date}")
        logger.info(f"{'='*60}\n")

        trades = engine.run_single_backtest(
            stock_id=args.stock_id,
            date=args.date,
            silent=args.no_chart
        )

        if trades:
            logger.info(f"\n完成回測，共 {len(trades)} 筆交易")
        else:
            logger.info("\n回測完成，無交易記錄")

    else:
        # 批次回測模式
        stock_list = [] 

        if args.use_screening:
            # 使用篩選結果
            stock_list = load_screening_results(args.date)
        elif args.stock_list:
            # 使用指定的股票列表
            stock_list = args.stock_list
        else:
            logger.error("批次模式需要指定 --stock_list 或使用 --use_screening")
            return

        if not stock_list:
            logger.error("沒有股票可供回測")
            return

        logger.info(f"\n{'='*60}")
        logger.info(f"執行批次回測")
        logger.info(f"股票數量: {len(stock_list)}, 日期: {args.date}")
        logger.info(f"{'='*60}\n")

        results = engine.run_batch_backtest(
            stock_list=stock_list,
            date=args.date,
            output_csv=not args.no_csv,
            create_charts=not args.no_chart
        )

        if results:
            logger.info(f"\n批次回測完成，共 {len(results)} 檔股票有交易")
        else:
            logger.info("\n批次回測完成，無交易記錄")

    logger.info("\n程式執行完成")


if __name__ == "__main__":
    main()