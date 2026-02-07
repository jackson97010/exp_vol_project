import pandas as pd
import os
from datetime import datetime

def query_stock_value(date, time, stock_code, base_path=r'D:\03_預估量相關資量\tw_kbar_1m_vol_exp'):
    """
    查詢特定日期、時間點、股票代碼的數值

    參數:
    - date: 日期字串，格式 'YYYYMMDD' 或 'YYYY-MM-DD'
    - time: 時間字串，格式 'HH:MM' 或 'HHMM'
    - stock_code: 股票代碼，如 '2330' 或 2330
    - base_path: 資料檔案的基礎路徑

    返回:
    - 該時間點的股票數值，如果找不到則返回 None
    """

    # 處理日期格式
    if '-' in str(date):
        date = date.replace('-', '')

    # 建構檔案路徑
    file_path = os.path.join(base_path, f'vol_exp_{date}.parquet')

    # 檢查檔案是否存在
    if not os.path.exists(file_path):
        print(f"錯誤: 找不到檔案 {file_path}")
        return None

    # 讀取資料
    df = pd.read_parquet(file_path)

    # 處理時間格式
    if ':' in str(time):
        time = time.replace(':', '')

    # 轉換股票代碼為字串
    stock_code = str(stock_code)

    # 查詢資料
    try:
        # 如果 k_time 是索引
        if df.index.name == 'k_time':
            if time in df.index and stock_code in df.columns:
                value = df.loc[time, stock_code]
                return value
            else:
                print(f"找不到資料: 時間={time}, 股票代碼={stock_code}")
                return None
        # 如果 k_time 是欄位
        else:
            if 'k_time' in df.columns:
                row = df[df['k_time'] == time]
                if not row.empty and stock_code in df.columns:
                    value = row[stock_code].iloc[0]
                    return value
                else:
                    print(f"找不到資料: 時間={time}, 股票代碼={stock_code}")
                    return None
    except Exception as e:
        print(f"查詢時發生錯誤: {e}")
        return None

def query_multiple_times(date, stock_code, times=None, base_path=r'D:\03_預估量相關資量\tw_kbar_1m_vol_exp'):
    """
    查詢某支股票在特定日期的多個時間點數值

    參數:
    - date: 日期字串
    - stock_code: 股票代碼
    - times: 時間點列表，如 ['0900', '0930', '1000']，如果為 None 則返回所有時間點
    - base_path: 資料檔案的基礎路徑

    返回:
    - DataFrame 或 Series 包含查詢結果
    """

    # 處理日期格式
    if '-' in str(date):
        date = date.replace('-', '')

    # 建構檔案路徑
    file_path = os.path.join(base_path, f'vol_exp_{date}.parquet')

    # 檢查檔案是否存在
    if not os.path.exists(file_path):
        print(f"錯誤: 找不到檔案 {file_path}")
        return None

    # 讀取資料
    df = pd.read_parquet(file_path)

    # 轉換股票代碼為字串
    stock_code = str(stock_code)

    # 檢查股票代碼是否存在
    if stock_code not in df.columns:
        print(f"錯誤: 找不到股票代碼 {stock_code}")
        return None

    # 如果指定時間點
    if times:
        # 處理時間格式
        times = [t.replace(':', '') if ':' in str(t) else str(t) for t in times]
        # 篩選指定時間點
        if df.index.name == 'k_time':
            result = df.loc[df.index.isin(times), stock_code]
        else:
            result = df[df['k_time'].isin(times)][['k_time', stock_code]]
    else:
        # 返回所有時間點
        if df.index.name == 'k_time':
            result = df[stock_code]
        else:
            result = df[['k_time', stock_code]]

    return result

def get_available_dates(base_path=r'D:\03_預估量相關資量\tw_kbar_1m_vol_exp'):
    """
    取得所有可用的日期列表
    """
    import glob

    pattern = os.path.join(base_path, 'vol_exp_*.parquet')
    files = glob.glob(pattern)

    dates = []
    for file in files:
        filename = os.path.basename(file)
        # 從檔名提取日期 (vol_exp_YYYYMMDD.parquet)
        if filename.startswith('vol_exp_') and filename.endswith('.parquet'):
            date = filename.replace('vol_exp_', '').replace('.parquet', '')
            dates.append(date)

    dates.sort()
    return dates

def get_available_stocks(date, base_path=r'D:\03_預估量相關資量\tw_kbar_1m_vol_exp'):
    """
    取得特定日期可用的股票代碼列表
    """
    # 處理日期格式
    if '-' in str(date):
        date = date.replace('-', '')

    # 建構檔案路徑
    file_path = os.path.join(base_path, f'vol_exp_{date}.parquet')

    # 檢查檔案是否存在
    if not os.path.exists(file_path):
        print(f"錯誤: 找不到檔案 {file_path}")
        return None

    # 讀取資料
    df = pd.read_parquet(file_path)

    # 返回欄位列表（股票代碼）
    return df.columns.tolist()

# 使用範例
if __name__ == "__main__":
    # 範例1: 查詢單一數值
    print("=== 查詢單一數值 ===")
    value = query_stock_value(
        date='20250901',  # 或 '2025-09-01'
        time='0930',      # 或 '09:30'
        stock_code='2330'  # 台積電
    )
    print(f"2330 在 2025-09-01 09:30 的數值: {value}")

    # 範例2: 查詢某支股票的多個時間點
    print("\n=== 查詢多個時間點 ===")
    result = query_multiple_times(
        date='20250901',
        stock_code='2330',
        times=['0900', '0930', '1000', '1100', '1300']
    )
    if result is not None:
        print(result)

    # 範例3: 查詢某支股票的所有時間點
    print("\n=== 查詢所有時間點 ===")
    all_times = query_multiple_times(
        date='20250901',
        stock_code='2330',
        times=None  # 不指定時間，取得所有時間點
    )
    if all_times is not None:
        print(f"資料筆數: {len(all_times)}")
        print(all_times.head())

    # 範例4: 取得可用的日期列表
    print("\n=== 可用的日期 ===")
    dates = get_available_dates()
    print(f"可用日期數量: {len(dates)}")
    if dates:
        print(f"最早日期: {dates[0]}")
        print(f"最新日期: {dates[-1]}")

    # 範例5: 取得特定日期的股票列表
    print("\n=== 可用的股票代碼 ===")
    stocks = get_available_stocks('20250901')
    if stocks:
        print(f"股票數量: {len(stocks)}")
        print(f"前10支股票: {stocks[:10]}")