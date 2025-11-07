# Data-Driven Member Partitioning

## Overview

This feature allows you to specify custom partition boundaries for member migration based on actual data distribution, rather than evenly dividing the time range. This is particularly useful when your data has uneven distribution across the `update_at` field.

## Problem

The original implementation divided members into partitions by evenly splitting the time range between `MIN(update_at)` and `MAX(update_at)`. However, if your data is unevenly distributed, some partitions may have significantly more records than others, leading to:

- Unbalanced workload across parallel producers
- Some producers finishing much earlier than others
- Suboptimal overall performance

### Example of Uneven Distribution

```
update_at <= '2024-01-01'                    →     360 members (0.03%)
'2024-01-01' < update_at <= '2025-01-01'     → 71,203,766 members (59.4%)
update_at > '2025-01-01'                     → 48,584,380 members (40.5%)
```

With even time-based partitioning, one partition would get 360 members while another gets 71M+ members.

## Solution

Use the `MemberPartitionBoundaries` configuration to specify custom partition boundaries based on your data distribution analysis.

## Configuration

Add the `MemberPartitionBoundaries` setting to your `appsettings.json`:

```json
{
  "Migration": {
    "Mode": "Embedding",
    "BatchSize": 1000,
    "ParallelMemberProducers": 3,
    "MemberPartitionBoundaries": ["2024-01-01", "2025-01-01"]
  }
}
```

This creates 3 partitions:
- **Partition 0**: `MIN(update_at)` to `2024-01-01`
- **Partition 1**: `2024-01-01` to `2025-01-01`
- **Partition 2**: `2025-01-01` to `MAX(update_at)`

## How to Determine Partition Boundaries

### Step 1: Analyze Your Data Distribution

Run queries to understand how your members are distributed:

```sql
-- Get overall count
SELECT COUNT(*) FROM members;

-- Get distribution by year
SELECT 
    DATE_TRUNC('year', update_at) AS year,
    COUNT(*) AS member_count
FROM members
GROUP BY DATE_TRUNC('year', update_at)
ORDER BY year;

-- Get specific ranges
SELECT COUNT(*) FROM members WHERE update_at <= '2024-01-01';
SELECT COUNT(*) FROM members WHERE update_at > '2024-01-01' AND update_at <= '2025-01-01';
SELECT COUNT(*) FROM members WHERE update_at > '2025-01-01';
```

### Step 2: Calculate Partition Boundaries

Aim to create partitions with approximately equal member counts. For example:

```
Total members: 120,000,000
Target: 3 parallel producers
Ideal partition size: ~40,000,000 members each

Based on analysis:
- Partition 0: up to 2024-01-01        →  20,000,000 members
- Partition 1: 2024-01-01 to 2025-01-01 → 60,000,000 members (split this further)
- Partition 2: 2025-01-01 onwards      →  40,000,000 members
```

You may need to adjust boundaries to split large ranges further:

```json
{
  "MemberPartitionBoundaries": ["2024-01-01", "2024-07-01", "2025-01-01"]
}
```

This creates 4 partitions with more balanced distribution.

### Step 3: Configure and Run

1. Update `appsettings.json` with your calculated boundaries
2. Set `ParallelMemberProducers` to match the number of partitions you want (boundaries + 1)
3. Run the migration

## Example Configuration

### Small Dataset (< 1M members)

```json
{
  "ParallelMemberProducers": 1,
  "MemberPartitionBoundaries": null
}
```

No need for partitioning; use default single producer.

### Medium Dataset with Even Distribution

```json
{
  "ParallelMemberProducers": 3,
  "MemberPartitionBoundaries": null
}
```

Let the system divide time range evenly; works well when data is uniformly distributed.

### Large Dataset with Uneven Distribution

Based on the example in the issue:

```json
{
  "ParallelMemberProducers": 3,
  "MemberPartitionBoundaries": ["2024-01-01", "2025-01-01"]
}
```

Creates:
- Partition 0: Minimal data (~360 members)
- Partition 1: Large dataset (~71M members) 
- Partition 2: Large dataset (~48M members)

Better approach - split the large ranges:

```json
{
  "ParallelMemberProducers": 4,
  "MemberPartitionBoundaries": ["2024-01-01", "2024-07-01", "2025-01-01"]
}
```

Or even more granular:

```json
{
  "ParallelMemberProducers": 6,
  "MemberPartitionBoundaries": [
    "2024-01-01",
    "2024-04-01", 
    "2024-07-01",
    "2024-10-01",
    "2025-01-01"
  ]
}
```

## Monitoring

When you run the migration with custom boundaries, the console output will show:

```
Creating member partitions based on update_at range:
  Min update_at: 2023-01-01 00:00:00
  Max update_at: 2025-12-31 23:59:59
Using 3 custom partition boundaries
  Partition 0: 2023-01-01 00:00:00 to 2024-01-01 00:00:00 (~360 members)
  Partition 1: 2024-01-01 00:00:00 to 2025-01-01 00:00:00 (~71,203,766 members)
  Partition 2: 2025-01-01 00:00:00 to 2025-12-31 23:59:59 (~48,584,380 members)

Using 3 parallel producers for members
```

This helps you verify that:
1. Boundaries are being applied correctly
2. Data distribution matches your expectations
3. Workload is reasonably balanced

## Benefits

1. **Balanced Workload**: Distribute work evenly across parallel producers
2. **Better Performance**: All producers finish at approximately the same time
3. **Flexibility**: Easily adjust based on your specific data patterns
4. **Transparency**: See actual member counts per partition in the console output

## Limitations

1. **Manual Analysis Required**: You need to analyze your data to determine optimal boundaries
2. **Static Boundaries**: Boundaries are fixed at migration start; data inserted during migration won't affect partitioning
3. **Checkpoint Compatibility**: When resuming from checkpoint, parallel producers are disabled (falls back to single producer)

## Troubleshooting

### Issue: Still Unbalanced Performance

**Symptom**: Some producers finish much faster than others

**Solution**:
- Re-analyze data distribution
- Add more boundaries to split large partitions
- Consider splitting the most populated time ranges into smaller segments

### Issue: Invalid Date Format Error

**Symptom**: `Invalid date format in MemberPartitionBoundaries` error

**Solution**:
- Ensure dates are in ISO 8601 format: `YYYY-MM-DD` or `YYYY-MM-DD HH:MM:SS`
- Examples: `"2024-01-01"`, `"2024-06-15 12:00:00"`

### Issue: Configuration Not Taking Effect

**Symptom**: System still using even time-based partitioning

**Solution**:
- Verify `MemberPartitionBoundaries` is not `null` or empty array
- Check JSON syntax is valid
- Ensure you're not resuming from a checkpoint (which disables parallel partitioning)

## Migration from Old Configuration

If you're currently using time-based partitioning and want to switch to data-driven:

1. **Analyze your data** using the SQL queries above
2. **Calculate boundaries** that balance member counts
3. **Update configuration** with `MemberPartitionBoundaries`
4. **Test with small dataset** first if possible
5. **Monitor the console output** during migration to verify distribution

## Fallback Behavior

If `MemberPartitionBoundaries` is:
- `null` (not specified)
- Empty array `[]`
- Only single value

The system will fall back to the original behavior: evenly dividing the time range.

This ensures backward compatibility with existing configurations.

## Related Configuration

This feature works in combination with:

- `ParallelMemberProducers`: Set to number of desired partitions (boundaries count + 1)
- `ConcurrentBatchProcessors`: Controls how many batches are processed concurrently
- `BatchSize`: Size of each batch read from PostgreSQL

See [PARALLEL_QUERY.md](PARALLEL_QUERY.md) for more information about parallel query processing.

## Performance Example

### Before (Even Time Partitioning)

```
ParallelMemberProducers: 3
Partitions by time:
  Partition 0: 360 members      (finishes in 1 second)
  Partition 1: 71,203,766 members (finishes in 35 minutes)
  Partition 2: 48,584,380 members (finishes in 24 minutes)
Total: ~35 minutes (limited by slowest partition)
```

### After (Data-Driven Partitioning)

```
ParallelMemberProducers: 5
Custom boundaries: ["2024-01-01", "2024-04-01", "2024-07-01", "2025-01-01"]
Partitions by data distribution:
  Partition 0: ~360 members       (finishes in 1 second)
  Partition 1: ~23,734,588 members (finishes in 12 minutes)
  Partition 2: ~23,734,589 members (finishes in 12 minutes)
  Partition 3: ~23,734,589 members (finishes in 12 minutes)
  Partition 4: ~48,584,380 members (finishes in 24 minutes)
Total: ~24 minutes (better balanced)
```

With further optimization (6 partitions):

```
All partitions: ~20M members each
All finish in: ~10-12 minutes
Total: ~12 minutes
```

## Conclusion

Data-driven partitioning allows you to optimize member migration for uneven data distributions. By analyzing your data and configuring appropriate boundaries, you can significantly improve migration performance and resource utilization.

For best results:
1. Analyze your data distribution
2. Calculate boundaries that create roughly equal-sized partitions
3. Test and monitor performance
4. Adjust boundaries as needed
