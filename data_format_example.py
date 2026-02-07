"""
台股一分鐘K線成交量資料 - 資料格式範例與說明
此檔案提供資料格式的完整範例，供後續 AI 理解資料結構

作者：Claude
日期：2026-02-07
"""

import pandas as pd
import os
from datetime import datetime

# ================================
# 資料基本資訊
# ================================

DATA_INFO = {
    "資料名稱": "台股一分鐘K線成交量資料",
    "資料位置": r"D:\03_預估量相關資量\tw_kbar_1m_vol_exp",
    "檔案格式": "Parquet",
    "檔名規則": "vol_exp_YYYYMMDD.parquet",
    "索引名稱": "datetime (原為 k_time)",
    "索引類型": "datetime64[us]",
    "欄位": "股票代碼（如：1101, 2330 等）",
    "資料內容": "成交量相關數值",
    "時間範圍": "09:00-13:30 (每分鐘)",
    "每日筆數": 266
}

# ================================
# 資料讀取範例
# ================================

def load_sample_data():
    """載入範例資料並顯示基本資訊"""

    # 範例檔案路徑
    file_path = r"D:\03_預估量相關資量\tw_kbar_1m_vol_exp\vol_exp_20250901.parquet"

    # 讀取資料
    df = pd.read_parquet(file_path)

    print("=" * 60)
    print("資料基本資訊")
    print("=" * 60)

    # 顯示資料形狀
    print(f"資料形狀: {df.shape}")
    print(f"  - 時間點數量: {df.shape[0]}")
    print(f"  - 股票數量: {df.shape[1]}")

    # 顯示索引資訊
    print(f"\n索引資訊:")
    print(f"  - 名稱: {df.index.name}")
    print(f"  - 類型: {df.index.dtype}")
    print(f"  - 起始時間: {df.index[0]}")
    print(f"  - 結束時間: {df.index[-1]}")

    # 顯示欄位資訊（前10支股票）
    print(f"\n股票代碼範例（前10支）:")
    for i, col in enumerate(df.columns[:10], 1):
        print(f"  {i:2}. {col}")

    return df

# ================================
# 資料結構展示
# ================================

def show_data_structure():
    """展示資料的詳細結構"""

    print("\n" + "=" * 60)
    print("資料結構範例")
    print("=" * 60)

    # 建立範例資料框架
    sample_structure = """
    資料結構示意圖:

    索引(datetime)           | 1101  | 1102  | 2330  | 2454  | ... |
    -------------------------|-------|-------|-------|-------|-----|
    2025-09-01 09:00:00     | 123   | 456   | 789   | 321   | ... |
    2025-09-01 09:01:00     | 124   | 457   | 790   | 322   | ... |
    2025-09-01 09:02:00     | 125   | 458   | 791   | 323   | ... |
    ...                     | ...   | ...   | ...   | ...   | ... |
    2025-09-01 13:30:00     | 200   | 500   | 850   | 400   | ... |

    說明：
    - 每一列(row)：代表一個時間點
    - 每一欄(column)：代表一支股票
    - 資料值：該股票在該時間點的成交量相關數值
    """

    print(sample_structure)

# ================================
# 查詢功能範例
# ================================

def query_examples(df):
    """展示各種查詢範例"""

    print("\n" + "=" * 60)
    print("查詢範例")
    print("=" * 60)

    # 範例1: 查詢特定時間、特定股票
    print("\n[範例1] 查詢 2330 在 09:30 的數值:")
    if '2330' in df.columns:
        target_time = pd.Timestamp('2025-09-01 09:30:00')
        if target_time in df.index:
            value = df.loc[target_time, '2330']
            print(f"  結果: {value}")

    # 範例2: 查詢特定股票的時間序列
    print("\n[範例2] 查詢 2330 的前5筆資料:")
    if '2330' in df.columns:
        series = df['2330'].head(5)
        for time, value in series.items():
            print(f"  {time}: {value}")

    # 範例3: 查詢特定時間的所有股票
    print("\n[範例3] 查詢 09:00 時前5支股票的數值:")
    target_time = pd.Timestamp('2025-09-01 09:00:00')
    if target_time in df.index:
        values = df.loc[target_time].head(5)
        for stock, value in values.items():
            print(f"  {stock}: {value}")

# ================================
# 資料統計資訊
# ================================

def show_statistics(df):
    """顯示資料的統計資訊"""

    print("\n" + "=" * 60)
    print("資料統計資訊")
    print("=" * 60)

    # 時間範圍統計
    print("\n時間範圍:")
    print(f"  起始: {df.index.min()}")
    print(f"  結束: {df.index.max()}")
    print(f"  總時間點: {len(df.index)}")

    # 股票統計
    print("\n股票統計:")
    print(f"  股票總數: {len(df.columns)}")

    # 資料完整性
    print("\n資料完整性:")
    null_count = df.isnull().sum().sum()
    total_cells = df.shape[0] * df.shape[1]
    print(f"  總資料點: {total_cells:,}")
    print(f"  缺失值: {null_count:,}")
    print(f"  完整率: {(1 - null_count/total_cells)*100:.2f}%")

# ================================
# 檔案列表功能
# ================================

def list_available_files():
    """列出所有可用的資料檔案"""

    print("\n" + "=" * 60)
    print("可用資料檔案")
    print("=" * 60)

    data_dir = r"D:\03_預估量相關資量\tw_kbar_1m_vol_exp"

    try:
        import glob
        pattern = os.path.join(data_dir, "vol_exp_*.parquet")
        files = glob.glob(pattern)
        files = [f for f in files if '_backup' not in f]
        files.sort()

        print(f"\n找到 {len(files)} 個資料檔案")

        if files:
            # 顯示日期範圍
            first_date = os.path.basename(files[0]).replace('vol_exp_', '').replace('.parquet', '')
            last_date = os.path.basename(files[-1]).replace('vol_exp_', '').replace('.parquet', '')

            print(f"日期範圍: {first_date} 至 {last_date}")

            # 顯示前5個檔案
            print("\n前5個檔案:")
            for f in files[:5]:
                filename = os.path.basename(f)
                size_mb = os.path.getsize(f) / (1024 * 1024)
                print(f"  - {filename} ({size_mb:.2f} MB)")

    except Exception as e:
        print(f"讀取檔案列表時發生錯誤: {e}")

# ================================
# 主程式
# ================================

def main():
    """主程式：展示所有資料資訊"""

    print("\n" + "="  60)
    print("台股一分鐘K線成交量資料 - 格式說明與範例")
    print("=" * 60)

    # 顯示資料基本資訊
    print("\n[基本資訊]")
    for key, value in DATA_INFO.items():
        print(f"  {key}: {value}")

    # 嘗試載入並分析資料
    try:
        # 載入範例資料
        df = load_sample_data()

        # 顯示資料結構
        show_data_structure()

        # 展示查詢範例
        query_examples(df)

        # 顯示統計資訊
        show_statistics(df)

    except FileNotFoundError:
        print("\n注意：找不到範例檔案，顯示結構說明")
        show_data_structure()
    except Exception as e:
        print(f"\n載入資料時發生錯誤: {e}")

    # 列出可用檔案
    list_available_files()

    print("\n" + "=" * 60)
    print("說明結束")
    print("=" * 60)

if __name__ == "__main__":
    # 執行主程式
    main()

    # 額外說明
    print("\n" + "=" * 60)
    print("給 AI 的使用說明")
    print("=" * 60)
    print("""
    1. 資料讀取：
       df = pd.read_parquet(r'D:\\03_預估量相關資量\\tw_kbar_1m_vol_exp\\vol_exp_YYYYMMDD.parquet')

    2. 查詢特定值：
       value = df.loc[datetime, stock_code]

    3. 取得時間序列：
       series = df[stock_code]

    4. 取得橫截面資料：
       snapshot = df.loc[datetime]

    5. 檔案路徑規則：
       將 YYYYMMDD 替換為實際日期即可

    6. 注意事項：
       - 索引名稱已從 'k_time' 改為 'datetime'
       - 索引是 datetime 類型，不是字串
       - 股票代碼是字串格式（如 '2330' 而非 2330）
    """)