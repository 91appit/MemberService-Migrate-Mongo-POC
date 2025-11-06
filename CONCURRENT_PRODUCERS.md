# Concurrent Producers Implementation (併行查詢實作)

## 問題說明 (Problem Statement)

原本的實作中，查詢 members 和 bundles 都使用 cursor pagination，但每個集合只有一個 producer 執行 while loop 從 PostgreSQL 讀取資料並寫入 channel 給 consumers。這個 PR 實作了**多個 concurrent producers**，讓查詢能夠併行執行，在確保資料正確性的前提下提升效能。

## 解決方案 (Solution)

### 核心概念：範圍分割 (Range-Based Partitioning)

為了確保多個 producers 能夠併行查詢而不會重複或遺漏資料，我們使用範圍分割策略：

1. **計算資料範圍**: 先取得資料的 min/max ID
2. **分割範圍**: 將 ID 範圍平均分配給 N 個 producers
3. **範圍查詢**: 每個 producer 只查詢自己負責的 ID 範圍
4. **無重疊**: 確保範圍之間沒有重疊，維持資料完整性

### 實作細節

#### 1. Bundles (Sequential Long IDs)

由於 bundles 使用 sequential long IDs（自動遞增），範圍分割很直接：

```sql
-- Producer 1
SELECT * FROM bundles WHERE id <= 1000 ORDER BY id LIMIT 1000;

-- Producer 2  
SELECT * FROM bundles WHERE id > 1000 AND id <= 2000 ORDER BY id LIMIT 1000;

-- Producer 3
SELECT * FROM bundles WHERE id > 2000 ORDER BY id LIMIT 1000;
```

#### 2. Members (UUID IDs)

Members 使用 UUID，雖然不是數字遞增，但 PostgreSQL 的 b-tree index 仍然有排序。我們使用取樣方式來分割：

1. 計算總 member 數量
2. 計算每個 producer 應該處理的數量 (total / numProducers)
3. 使用 OFFSET 取樣邊界 UUID
4. 每個 producer 查詢自己的 UUID 範圍

```sql
-- 取樣邊界 UUID (只在初始化時執行一次)
SELECT id FROM members ORDER BY id OFFSET 10000 LIMIT 1;  -- 第一個邊界
SELECT id FROM members ORDER BY id OFFSET 20000 LIMIT 1;  -- 第二個邊界

-- Producer 1
SELECT * FROM members WHERE id <= '<uuid_at_33%>' ORDER BY id LIMIT 1000;

-- Producer 2
SELECT * FROM members WHERE id > '<uuid_at_33%>' AND id <= '<uuid_at_66%>' ORDER BY id LIMIT 1000;

-- Producer 3
SELECT * FROM members WHERE id > '<uuid_at_66%>' ORDER BY id LIMIT 1000;
```

### 架構變更

#### Before (Single Producer)
```
PostgreSQL                   Channel              MongoDB
    |                           |                    |
    |---> Producer -----------> |                    |
    |     (Sequential)          |                    |
    |                           |---> Consumer 1 --->|
    |                           |---> Consumer 2 --->|
    |                           |---> Consumer 3 --->|
```

#### After (Multiple Producers)
```
PostgreSQL                   Channel              MongoDB
    |                           |                    |
    |---> Producer 1 ---------> |                    |
    |     (Range 1)             |                    |
    |                           |---> Consumer 1 --->|
    |---> Producer 2 ---------> |                    |
    |     (Range 2)             |                    |
    |                           |---> Consumer 2 --->|
    |---> Producer 3 ---------> |                    |
    |     (Range 3)             |                    |
    |                           |---> Consumer 3 --->|
```

## 程式碼變更 (Code Changes)

### 1. Configuration (AppSettings.cs)

新增 `ConcurrentProducers` 參數：

```csharp
public class MigrationSettings
{
    // ... existing properties ...
    public int ConcurrentProducers { get; set; } = 1;  // NEW
}
```

### 2. PostgreSQL Repository

新增範圍查詢方法：

```csharp
// 取得 ID 範圍
Task<(Guid? minId, Guid? maxId)> GetMembersIdRangeAsync()
Task<(long? minId, long? maxId)> GetBundlesIdRangeAsync()

// 取樣 IDs 用於分割
Task<List<Guid>> GetMemberIdSamplesAsync(int numSamples, long interval)
Task<List<long>> GetBundleIdSamplesAsync(int numSamples, long interval)

// 範圍查詢
Task<List<Member>> GetMembersBatchInRangeAsync(Guid? lastMemberId, Guid? maxId, int limit)
Task<List<Bundle>> GetBundlesBatchInRangeAsync(long? lastBundleId, long? maxId, int limit)
```

### 3. Migration Service

新增範圍計算與多 producer 邏輯：

```csharp
// Helper classes for ranges
internal class MemberIdRange { ... }
internal class BundleIdRange { ... }

// 範圍計算
private async Task<List<MemberIdRange>> CalculateMemberIdRangesAsync(int numProducers)
private async Task<List<BundleIdRange>> CalculateBundleIdRangesAsync(int numProducers)

// 多 producer 實作
// - MigrateMembersWithoutBundlesConcurrentlyAsync: 建立多個 producer tasks
// - MigrateBundlesAndUpdateMembersConcurrentlyAsync: 建立多個 producer tasks
// - MigrateMembersConcurrentlyAsync: 建立多個 producer tasks
// - MigrateBundlesConcurrentlyAsync: 建立多個 producer tasks
```

## 使用方式 (Usage)

### Configuration

在 `appsettings.json` 中設定：

```json
{
  "Migration": {
    "ConcurrentProducers": 2,  // 1 到 3 之間
    "ConcurrentBatchProcessors": 3,
    "MaxChannelCapacity": 10,
    "BatchSize": 1000
  }
}
```

### 建議設定

| 系統資源 | ConcurrentProducers | ConcurrentBatchProcessors |
|---------|---------------------|---------------------------|
| 高效能 (32GB+ RAM, 8+ cores) | 3 | 5 |
| 中等 (16GB RAM, 4-8 cores) | 2 | 3 |
| 低資源 (8GB RAM, 2-4 cores) | 1 | 2 |

## 效能提升 (Performance Improvements)

預期效能提升：

- **ConcurrentProducers = 1**: 基準 (100%)
- **ConcurrentProducers = 2**: 120-150% (20-50% 更快)
- **ConcurrentProducers = 3**: 150-180% (50-80% 更快)

實際效能取決於：
- PostgreSQL 的 CPU 和 I/O 能力
- 網路頻寬和延遲
- 資料分佈的均勻程度
- PostgreSQL 的 max_connections 設定

## 注意事項 (Considerations)

### 何時使用多個 Producers

✅ **適合使用** (2-3 producers):
- PostgreSQL 有足夠的 CPU 和 I/O 能力
- 網路頻寬充足 (1Gbps+)
- PostgreSQL max_connections 足夠
- 需要更快完成遷移
- 系統有足夠的 CPU cores (4+)

⚠️ **建議單一 Producer** (1 producer):
- PostgreSQL 已經負載很重
- 網路延遲高或頻寬有限
- PostgreSQL connection pool 受限
- 系統資源有限
- 想在營業時間減少對 PostgreSQL 的影響

### 資料正確性保證

1. **無重疊**: 每個 producer 的範圍互斥，不會查詢到重複資料
2. **無遺漏**: 範圍覆蓋完整，不會遺漏任何資料
3. **順序性**: 在同一個 producer 內維持 cursor pagination 的順序
4. **冪等性**: MongoDB 使用 `IsOrdered = false` 的 bulk insert，即使重試也不會重複

## 測試 (Testing)

由於沒有現有的測試基礎設施，建議手動測試：

1. **單一 Producer**: 設定 `ConcurrentProducers = 1`，確認功能正常
2. **多 Producers**: 設定 `ConcurrentProducers = 2`，驗證資料完整性
3. **效能比較**: 比較不同 ConcurrentProducers 設定的遷移時間

## 向後相容性 (Backward Compatibility)

- 預設值為 `ConcurrentProducers = 1`，與原本行為相同
- 現有的 configuration 不需要修改即可運作
- 可以選擇性地增加 `ConcurrentProducers` 參數來啟用新功能

## 結論 (Conclusion)

這個實作透過範圍分割策略，成功實現了多個 concurrent producers 併行查詢 PostgreSQL，在維持資料正確性的前提下，提供了顯著的效能提升空間。使用者可以根據自己的系統資源和需求，彈性調整 concurrent producers 的數量。
