import pandas as pd
import numpy as np
from finlab import data

# =============================================================================
# 1. 設定與資料獲取
# =============================================================================
START_DATE = '2024-07-31'
END_DATE = '2026-02-05'

print("正在從 FinLab 獲取全市場歷史資料...")
close = data.get('price:收盤價')
turnover = data.get('price:成交金額')

# =============================================================================
# 2. 核心邏輯計算 (向量化運算)
# =============================================================================

# --- A. 計算 20 日平均成交金額 (不含當日 T, 取 T-1 ~ T-20) ---
avg_turnover_20d = turnover.shift(1).rolling(20).mean()

# --- B. 計算漲停狀態 ---
def calculate_limit_up_df(df_prev_close):
    """計算台股 DataFrame 的漲停價"""
    res = df_prev_close * 1.1
    tick = pd.DataFrame(0.01, index=res.index, columns=res.columns)
    tick[res >= 10] = 0.05
    tick[res >= 50] = 0.1
    tick[res >= 100] = 0.5
    tick[res >= 500] = 1
    tick[res >= 1000] = 5
    return (np.floor((res + 0.0001) / tick) * tick).round(2)

limit_up_prices = calculate_limit_up_df(close.shift(1))

# T 日當天是否漲停
is_limit_up = (close == limit_up_prices)

# T-1 日是否漲停 (將狀態往後移一格)
is_prev_limit_up = is_limit_up.shift(1)

# =============================================================================
# 3. 資料整理與合併 (解決 KeyError 問題)
# =============================================================================

print("正在整理並合併資料欄位...")

def to_long(df, name):
    # 明確命名索引，避免 reset_index 產生錯誤名稱
    df.index.name = 'date'
    df.columns.name = 'stock_id'
    return df.stack().reset_index().rename(columns={0: name})

df_close = to_long(close, 'close')
df_avg = to_long(avg_turnover_20d, 'avg_turnover_20d')
df_limit = to_long(is_limit_up, 'is_limit_up')
df_prev_limit = to_long(is_prev_limit_up, 'is_prev_limit_up')

# 合併所有 DataFrame
final_df = df_close.merge(df_avg, on=['date', 'stock_id'], how='left') \
                   .merge(df_limit, on=['date', 'stock_id'], how='left') \
                   .merge(df_prev_limit, on=['date', 'stock_id'], how='left')

# =============================================================================
# 4. 條件過濾與整理
# =============================================================================

# 1. 限制日期範圍
final_df = final_df[(final_df['date'] >= START_DATE) & (final_df['date'] <= END_DATE)]

# 2. 剔除 ETF (00開頭) 與 非四位數代碼 (權證等)
final_df = final_df[~final_df['stock_id'].str.startswith('00')]
final_df = final_df[final_df['stock_id'].str.len() == 4]

# 3. 處理空值 (前 20 天無平均值者不列入)
final_df = final_df.dropna(subset=['avg_turnover_20d'])

# =============================================================================
# 5. 輸出 Parquet
# =============================================================================
output_filename = f"stock_volume.parquet"
final_df.to_parquet(output_filename, index=False)

print("\n" + "=" * 60)
print(f"✅ 歷史成交資料 Parquet 已產出")
print(f"📂 檔案名稱: {output_filename}")
print(f"📊 總筆數: {len(final_df)} 筆")
print("-" * 60)
print("欄位說明:")
print(" - avg_turnover_20d : T-1 ~ T-20 的平均成交金額 (元)")
print(" - is_limit_up      : T 日當天收盤是否漲停")
print(" - is_prev_limit_up : T-1 日收盤是否漲停")
print("=" * 60)

# 預覽資料
print(final_df.tail())