"""
常數定義模組
集中管理所有系統常數和設定
"""
import os

# ===== 路徑設定 =====
# 輸出路徑設定
OUTPUT_BASE_DIR = r"D:\回測結果"

# 篩選結果路徑
SCREENING_RESULTS_PATH = r"C:\Users\User\Documents\_02_bt\Backtest_tick_module\screening_results.csv"

# 總交易輸出檔名
TOTAL_TRADES_OUTPUT = os.path.join(OUTPUT_BASE_DIR, "backtest_trades_total.csv")

# 預設設定檔
DEFAULT_CONFIG_PATH = "Bo_v2.yaml"

# ===== 交易參數 =====
# 預設交易股數（1張 = 1000股）
DEFAULT_SHARES_PER_TRADE = 1000

# 漲幅上限
MAX_GAIN_PERCENTAGE = 8.5

# 開盤時間限制（9:05前不進場）
MARKET_OPEN_TIME_LIMIT = "09:05:00"

# 收盤時間
MARKET_CLOSE_TIME = "13:30:00"

# ===== 技術指標參數 =====
# Day High 動能追蹤窗口（秒）
DAY_HIGH_MOMENTUM_WINDOW = 60

# 外盤追蹤窗口（秒）
OUTSIDE_VOLUME_WINDOW = 3

# 大量搓合追蹤窗口（秒）
MASSIVE_MATCHING_WINDOW = 1

# 內外盤比追蹤窗口（秒）
IO_RATIO_WINDOW = 60

# 大單門檻（張）
LARGE_ORDER_THRESHOLD = 10

# 委買賣平衡門檻
ORDER_BOOK_THIN_THRESHOLD = 20
ORDER_BOOK_NORMAL_THRESHOLD = 40

# ===== Buffer 機制參數 =====
# Buffer 持續時間（秒）
BUFFER_DURATION_SECONDS = 3

# ===== 日誌設定 =====
LOG_FORMAT = '%(asctime)s - %(levelname)s - %(message)s'
LOG_DATE_FORMAT = '%H:%M:%S'
