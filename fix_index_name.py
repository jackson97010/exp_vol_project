import pandas as pd
import os
import glob
from tqdm import tqdm

def fix_index_name(file_path, backup=True):
    """
    修正索引名稱為 'datetime'

    Args:
        file_path: parquet 檔案路徑
        backup: 是否備份原始檔案

    Returns:
        bool: 是否成功修正
    """
    try:
        filename = os.path.basename(file_path)
        print(f"處理: {filename}")

        # 讀取檔案
        df = pd.read_parquet(file_path)

        # 檢查當前索引名稱
        print(f"  當前索引名稱: {df.index.name}")
        print(f"  索引類型: {df.index.dtype}")

        # 如果索引名稱不是 datetime，則修正
        if df.index.name != 'datetime':
            # 備份原始檔案（如果需要）
            if backup:
                backup_path = file_path.replace('.parquet', '_index_backup.parquet')
                if not os.path.exists(backup_path):
                    df_backup = df.copy()
                    df_backup.to_parquet(backup_path)
                    print(f"  已備份至: {os.path.basename(backup_path)}")

            # 修正索引名稱
            df.index.name = 'datetime'

            # 儲存修正後的檔案
            df.to_parquet(file_path)
            print(f"  ✓ 索引名稱已修正為: {df.index.name}")
            return True
        else:
            print(f"  ○ 索引名稱已經是 'datetime'，跳過")
            return True

    except Exception as e:
        print(f"  ✗ 錯誤: {str(e)}")
        return False

def batch_fix_index_names(
    data_dir=r'D:\03_預估量相關資量\tw_kbar_1m_vol_exp',
    test_mode=False,
    backup=True
):
    """
    批次修正所有檔案的索引名稱

    Args:
        data_dir: 資料目錄
        test_mode: 測試模式（只處理前3個檔案）
        backup: 是否備份

    Returns:
        dict: 處理結果統計
    """
    # 取得所有 parquet 檔案
    pattern = os.path.join(data_dir, 'vol_exp_*.parquet')
    files = glob.glob(pattern)

    # 排除備份檔案
    files = [f for f in files if '_backup' not in f and '_index_backup' not in f]

    print(f"找到 {len(files)} 個檔案需要檢查")

    if test_mode:
        files = files[:3]
        print(f"測試模式：只處理前 {len(files)} 個檔案\n")

    # 處理統計
    success = 0
    failed = 0
    skipped = 0

    # 批次處理
    print("開始批次處理...\n")
    for file_path in tqdm(files, desc="處理進度"):
        result = fix_index_name(file_path, backup=backup)
        if result:
            success += 1
        else:
            failed += 1
        print("")  # 換行

    # 顯示結果
    print("\n" + "="*60)
    print("處理完成！")
    print(f"  成功: {success}")
    print(f"  失敗: {failed}")
    print(f"  總計: {len(files)}")

    return {
        'success': success,
        'failed': failed,
        'total': len(files)
    }

def verify_index_names(data_dir=r'D:\03_預估量相關資量\tw_kbar_1m_vol_exp', sample_count=5):
    """
    驗證檔案的索引名稱

    Args:
        data_dir: 資料目錄
        sample_count: 要驗證的檔案數量
    """
    pattern = os.path.join(data_dir, 'vol_exp_*.parquet')
    files = glob.glob(pattern)
    files = [f for f in files if '_backup' not in f and '_index_backup' not in f][:sample_count]

    print(f"驗證前 {len(files)} 個檔案的索引名稱：\n")

    for file_path in files:
        filename = os.path.basename(file_path)
        try:
            df = pd.read_parquet(file_path)
            print(f"{filename:30} | 索引名稱: {df.index.name:10} | 類型: {str(df.index.dtype):15}")
        except Exception as e:
            print(f"{filename:30} | 錯誤: {str(e)}")

    print("\n" + "="*60)

def remove_index_backups(data_dir=r'D:\03_預估量相關資量\tw_kbar_1m_vol_exp', confirm=False):
    """
    清理索引備份檔案

    Args:
        data_dir: 資料目錄
        confirm: 是否確認刪除
    """
    if not confirm:
        print("⚠️ 警告：這將刪除所有 *_index_backup.parquet 檔案")
        print("如要執行，請設定 confirm=True")
        return

    pattern = os.path.join(data_dir, '*_index_backup.parquet')
    backup_files = glob.glob(pattern)

    deleted = 0
    for file_path in backup_files:
        try:
            os.remove(file_path)
            print(f"已刪除: {os.path.basename(file_path)}")
            deleted += 1
        except Exception as e:
            print(f"刪除失敗 {os.path.basename(file_path)}: {e}")

    print(f"\n已刪除 {deleted} 個備份檔案")

if __name__ == "__main__":
    print("="*60)
    print("修正索引名稱為 'datetime'")
    print("="*60)

    # 步驟1: 驗證當前狀態
    print("\n[步驟1] 驗證當前索引名稱：")
    verify_index_names(sample_count=5)

    # 步驟2: 測試模式修正
    print("\n[步驟2] 測試模式修正（前3個檔案）：")
    result = batch_fix_index_names(test_mode=True, backup=True)

    # 步驟3: 再次驗證
    print("\n[步驟3] 驗證修正結果：")
    verify_index_names(sample_count=3)

    # 步驟4: 完整批次處理（取消註解以執行）
    # print("\n[步驟4] 處理所有檔案：")
    # result = batch_fix_index_names(test_mode=False, backup=True)

    # 步驟5: 清理備份（取消註解以執行）
    # print("\n[步驟5] 清理備份檔案：")
    # remove_index_backups(confirm=True)