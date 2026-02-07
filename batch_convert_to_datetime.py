import pandas as pd
import os
import glob
from datetime import datetime, time
import numpy as np
from tqdm import tqdm

def convert_time_to_datetime(time_str, date_str):
    """
    將時間字串轉換為 datetime 物件

    Args:
        time_str: 時間字串 (格式: 'HHMM' 或 'HH:MM')
        date_str: 日期字串 (格式: 'YYYYMMDD')

    Returns:
        datetime 物件
    """
    # 處理時間字串
    time_str = str(time_str).zfill(4)  # 確保是4位數

    # 解析日期
    year = int(date_str[:4])
    month = int(date_str[4:6])
    day = int(date_str[6:8])

    # 解析時間
    hour = int(time_str[:2])
    minute = int(time_str[2:4])

    return datetime(year, month, day, hour, minute)

def process_single_file(file_path, output_path=None, keep_backup=True):
    """
    處理單一 parquet 檔案，將 k_time 索引轉換為 datetime

    Args:
        file_path: 輸入檔案路徑
        output_path: 輸出檔案路徑（如果為 None，則覆蓋原檔案）
        keep_backup: 是否保留原始檔案備份

    Returns:
        bool: 是否成功處理
    """
    try:
        # 從檔案名稱提取日期
        filename = os.path.basename(file_path)
        if filename.startswith('vol_exp_') and filename.endswith('.parquet'):
            date_str = filename.replace('vol_exp_', '').replace('.parquet', '')
        else:
            print(f"無法從檔案名稱提取日期: {filename}")
            return False

        print(f"處理檔案: {filename}")

        # 讀取資料
        df = pd.read_parquet(file_path)

        # 檢查索引名稱
        if df.index.name == 'k_time':
            # 轉換索引為 datetime
            print(f"  原始索引類型: {df.index.dtype}")
            print(f"  原始索引範例: {df.index[:3].tolist()}")

            # 將索引轉換為 datetime
            datetime_index = pd.DatetimeIndex([
                convert_time_to_datetime(t, date_str) for t in df.index
            ])
            df.index = datetime_index
            df.index.name = 'datetime'

            print(f"  轉換後索引類型: {df.index.dtype}")
            print(f"  轉換後索引範例: {df.index[:3].tolist()}")

            # 備份原始檔案
            if keep_backup and output_path is None:
                backup_path = file_path.replace('.parquet', '_backup.parquet')
                if not os.path.exists(backup_path):
                    os.rename(file_path, backup_path)
                    print(f"  已備份原始檔案至: {backup_path}")

            # 儲存檔案
            if output_path:
                df.to_parquet(output_path)
                print(f"  已儲存至: {output_path}")
            else:
                df.to_parquet(file_path)
                print(f"  已更新原始檔案")

            return True

        elif df.index.name == 'datetime' or df.index.name == 'time':
            print(f"  索引已經是 {df.index.name}，跳過處理")
            return True
        else:
            print(f"  未預期的索引名稱: {df.index.name}")
            return False

    except Exception as e:
        print(f"處理檔案 {file_path} 時發生錯誤: {str(e)}")
        return False

def batch_convert_all_files(
    input_dir=r'D:\03_預估量相關資量\tw_kbar_1m_vol_exp',
    output_dir=None,
    keep_backup=True,
    test_mode=False
):
    """
    批次處理所有 parquet 檔案

    Args:
        input_dir: 輸入目錄
        output_dir: 輸出目錄（如果為 None，則覆蓋原檔案）
        keep_backup: 是否保留原始檔案備份
        test_mode: 測試模式（只處理第一個檔案）

    Returns:
        dict: 處理結果統計
    """
    # 取得所有 parquet 檔案
    pattern = os.path.join(input_dir, 'vol_exp_*.parquet')
    files = glob.glob(pattern)

    # 排除備份檔案
    files = [f for f in files if '_backup' not in f]

    print(f"找到 {len(files)} 個檔案待處理")

    if test_mode and files:
        print("\n=== 測試模式：只處理第一個檔案 ===")
        files = files[:1]

    # 如果指定輸出目錄，確保目錄存在
    if output_dir and not os.path.exists(output_dir):
        os.makedirs(output_dir)
        print(f"建立輸出目錄: {output_dir}")

    # 處理統計
    success_count = 0
    failed_count = 0
    skipped_count = 0

    # 批次處理
    for file_path in tqdm(files, desc="處理進度"):
        # 決定輸出路徑
        if output_dir:
            filename = os.path.basename(file_path)
            output_path = os.path.join(output_dir, filename)
        else:
            output_path = None

        # 處理檔案
        success = process_single_file(file_path, output_path, keep_backup)

        if success:
            success_count += 1
        else:
            failed_count += 1

        print("")  # 換行

    # 顯示統計
    print("\n=== 處理完成 ===")
    print(f"成功: {success_count} 個檔案")
    print(f"失敗: {failed_count} 個檔案")

    return {
        'success': success_count,
        'failed': failed_count,
        'total': len(files)
    }

def verify_conversion(file_path):
    """
    驗證檔案是否已成功轉換

    Args:
        file_path: 檔案路徑
    """
    try:
        df = pd.read_parquet(file_path)
        print(f"檔案: {os.path.basename(file_path)}")
        print(f"索引名稱: {df.index.name}")
        print(f"索引類型: {df.index.dtype}")
        print(f"索引範例:")
        print(df.index[:5])
        print(f"資料形狀: {df.shape}")
        return True
    except Exception as e:
        print(f"驗證失敗: {str(e)}")
        return False

def restore_from_backup(input_dir=r'D:\03_預估量相關資量\tw_kbar_1m_vol_exp'):
    """
    從備份還原原始檔案

    Args:
        input_dir: 目錄路徑
    """
    pattern = os.path.join(input_dir, '*_backup.parquet')
    backup_files = glob.glob(pattern)

    print(f"找到 {len(backup_files)} 個備份檔案")

    for backup_path in backup_files:
        original_path = backup_path.replace('_backup.parquet', '.parquet')

        # 刪除現有檔案
        if os.path.exists(original_path):
            os.remove(original_path)

        # 還原備份
        os.rename(backup_path, original_path)
        print(f"已還原: {os.path.basename(original_path)}")

    print("還原完成")

if __name__ == "__main__":
    print("=== k_time 轉 datetime 批次處理程式 ===\n")

    # 選項1: 測試模式（只處理一個檔案）
    print("1. 測試單一檔案轉換")
    result = batch_convert_all_files(
        test_mode=True,
        keep_backup=True
    )

    # 驗證轉換結果
    print("\n2. 驗證轉換結果")
    test_file = r'D:\03_預估量相關資量\tw_kbar_1m_vol_exp\vol_exp_20250901.parquet'
    if os.path.exists(test_file):
        verify_conversion(test_file)

    # 選項2: 處理所有檔案（取消註解以執行）
    # print("\n3. 處理所有檔案")
    # result = batch_convert_all_files(
    #     keep_backup=True,
    #     test_mode=False
    # )

    # 選項3: 輸出到新目錄（取消註解以執行）
    # print("\n4. 輸出到新目錄")
    # result = batch_convert_all_files(
    #     output_dir=r'D:\03_預估量相關資量\tw_kbar_1m_vol_exp_datetime',
    #     keep_backup=False,
    #     test_mode=False
    # )

    # 選項4: 還原備份（如果需要）
    # print("\n5. 還原所有備份")
    # restore_from_backup()