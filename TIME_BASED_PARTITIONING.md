# Time-Based Partitioning Feature

## Overview

The migration tool now supports time-based partitioning to handle uneven data distribution in the `members` table based on the `update_at` field. This feature allows you to split the migration into multiple partitions based on date boundaries, ensuring more efficient processing of large datasets with skewed distributions.

## Configuration

### appsettings.json

Add the `UpdateAtPartitions` array to the `Migration` section:

```json
{
  "Migration": {
    "Mode": "Embedding",
    "BatchSize": 1000,
    "MaxDegreeOfParallelism": 4,
    "ConcurrentBatchProcessors": 3,
    "MaxChannelCapacity": 10,
    "UpdateAtPartitions": ["2024-01-01", "2025-01-01"]
  }
}
```

### Partition Definition

The `UpdateAtPartitions` array defines the boundaries between partitions. The system will automatically create:

1. **First partition**: All records with `update_at <= first_boundary`
2. **Middle partitions**: Records between consecutive boundaries
3. **Last partition**: All records with `update_at > last_boundary`

#### Example with ["2024-01-01", "2025-01-01"]

This creates 3 partitions:

- **Partition 0**: `update_at <= '2024-01-01'`
- **Partition 1**: `'2024-01-01' < update_at <= '2025-01-01'`
- **Partition 2**: `update_at > '2025-01-01'`

## Use Case

Based on the actual data distribution in the members table:

| Partition | Date Range | Record Count |
|-----------|------------|--------------|
| 0 | ≤ 2024-01-01 | 360 |
| 1 | 2024-01-01 to 2025-01-01 | 71,203,766 |
| 2 | > 2025-01-01 | 48,584,380 |

With partitioning enabled, the migration tool will:
1. Process partition 0 (360 records) quickly
2. Process partition 1 (71M records) in batches
3. Process partition 2 (48M records) in batches

## Migration Behavior

### Startup Information

When partitioning is enabled, the tool displays partition information at startup:

```
=== Time-based Partitioning Enabled ===
Total partitions: 3
Partition 0: <= 2024-01-01 - 360 members
Partition 1: 2024-01-01 to 2025-01-01 - 71,203,766 members
Partition 2: > 2025-01-01 - 48,584,380 members
```

### Progress Logging

During migration, the console output shows which partition is being processed:

```
Processing partition 0: <= 2024-01-01
[Member Batch 1, Partition 0] Processed 360 members in 0.23s
Progress: 360/120000000 members (0.00%) - Est. remaining: 02:15:30

Processing partition 1: 2024-01-01 to 2025-01-01
[Member Batch 2, Partition 1] Processed 1000 members in 1.45s
Progress: 1360/120000000 members (0.00%) - Est. remaining: 02:15:28
...
```

### Checkpoint and Resume

The checkpoint file now includes the partition index:

```json
{
  "Mode": "Embedding",
  "LastMemberId": "12345678-1234-1234-1234-123456789abc",
  "CurrentPartitionIndex": 1,
  "Timestamp": "2025-01-07T12:00:00Z",
  "Status": "Phase1-MigratingMembers"
}
```

If the migration is interrupted and resumed, it will:
1. Start from the saved partition index
2. Skip completed partitions
3. Resume from the last processed member ID within that partition

## Benefits

1. **Better Progress Visibility**: Clear indication of which time range is being processed
2. **Predictable Performance**: Each partition can be optimized based on its size
3. **Flexible Data Distribution**: Adjust boundaries to match your actual data distribution
4. **Checkpoint Granularity**: Resume from the exact partition if interrupted
5. **Query Performance**: Smaller time ranges may benefit from better index usage

## Backward Compatibility

If `UpdateAtPartitions` is not configured or is an empty array, the migration tool will:
- Create a single partition covering all data
- Behave exactly as before (using simple cursor pagination by ID)
- Still use checkpoint and resume functionality

## Advanced Configuration Examples

### Example 1: Very Skewed Distribution
```json
{
  "UpdateAtPartitions": ["2023-01-01", "2024-01-01", "2024-06-01", "2025-01-01"]
}
```

Creates 5 partitions with finer granularity for better control.

### Example 2: Single Boundary
```json
{
  "UpdateAtPartitions": ["2024-07-01"]
}
```

Creates 2 partitions: before and after July 2024.

### Example 3: Quarterly Partitions
```json
{
  "UpdateAtPartitions": ["2024-04-01", "2024-07-01", "2024-10-01", "2025-01-01"]
}
```

Creates 5 quarterly partitions for 2024-2025.

## Technical Details

### Database Queries

The tool uses the following query pattern for each partition:

```sql
SELECT id, password, salt, tenant_id, state, allow_login,
       create_at, create_user, update_at, update_user, version,
       extensions, tags, profile, tags_v2
FROM members
WHERE update_at > @startDate
  AND update_at <= @endDate
  AND id > @lastId
ORDER BY id
LIMIT @limit
```

### Index Recommendations

For optimal performance, ensure an index exists on `(update_at, id)`:

```sql
CREATE INDEX ix_members_update_at_id ON members(update_at, id);
```

This composite index allows efficient range scans and cursor pagination within each partition.

## Applies To

- Both **Embedding** and **Referencing** migration modes
- Phase 1 of Embedding mode (member migration)
- Member migration in Referencing mode
- Bundle migration continues to use ID-based pagination (not affected by this feature)
