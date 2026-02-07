**Markdown**

# 📊 台股 1m K線資料快速手冊

## 📂 檔案定義

- **路徑**: `D:\tw_kbar_1m_vol_exp\vol_exp_YYYYMMDD.parquet`
- **結構**:
  - **Index**: `datetime` (Datetime64)
  - **Columns**: 股票代碼 (String, e.g., `'2330'`)
- **時間**: 09:00 - 13:30

---

## 💻 核心操作 (Python)

### 1. 載入資料

```python
import pandas as pd
df = pd.read_parquet(r'`D:\tw_kbar_1m_vol_exp\vol_exp_YYYYMMDD.parquet`')
```

### 2. 資料選取

| **目標**     | **程式碼片段**                           |
| ------------------ | ---------------------------------------------- |
| **單一數值** | `val = df.at['2025-09-01 09:30:00', '2330']` |
| **單一個股** | `stock = df['2330']`                         |
| **特定分鐘** | `snapshot = df.loc['2025-09-01 09:30:00']`   |
| **時間區間** | `df.between_time('09:00', '10:00')`          |

---

## ⚠️ 開發細節

1. **型別檢查** : 股票代碼必須為 **字串** (String)，例如 `'2330'`。
2. **索引格式** : 索引已統一由 `k_time` 改為 `datetime` 類型。
3. **效能優化** : 查詢單一數值時，使用 `.at[]` 速度優於 `.loc[]`。
---

**程式化處理時使用 data_format_spec.json**
   ```python
   import json
   with open('data_format_spec.json', 'r', encoding='utf-8') as f:
       spec = json.load(f)
   ```

