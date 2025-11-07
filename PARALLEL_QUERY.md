# Parallel Query Processing (並行查詢)

## Overview

This feature enables parallel query execution for members and bundles data migration, significantly improving throughput by running multiple concurrent producers that read from different data partitions.

## Background

Previously, the migration used a single producer with cursor-based pagination in a while loop, continuously reading data and writing to a channel consumed by multiple workers. While this approach utilized multiple consumers, the producer was a bottleneck as only one query could run at a time against PostgreSQL.

The parallel query feature introduces **multiple concurrent producers**, each reading from a different data partition, maximizing database throughput and reducing overall migration time.

## Partitioning Strategy

### Members Partitioning (by `update_at`)

Members are partitioned based on the `update_at` timestamp field:

1. Query `MIN(update_at)` and `MAX(update_at)` from the members table
2. Divide the timestamp range into N equal partitions (where N = `ParallelMemberProducers`)
3. Each producer queries a specific time range: `WHERE update_at >= @start AND update_at < @end`
4. Within each partition, cursor pagination (`id > last_id`) is still used for consistency

**Why `update_at`?**
- Provides relatively even data distribution across time
- Indexed field (existing `ix_members_update_at` index)
- Natural temporal partitioning that reflects data creation patterns

### Bundles Partitioning (by `id`)

Bundles are partitioned based on the `id` field (sequential number):

1. Query `MIN(id)` and `MAX(id)` from the bundles table
2. Divide the ID range into N equal partitions (where N = `ParallelBundleProducers`)
3. Each producer queries a specific ID range: `WHERE id >= @startId AND id < @endId`
4. Within each partition, cursor pagination (`id > last_id`) is still used

**Why `id`?**
- Bundles use auto-incrementing sequential IDs
- Simpler and more predictable than timestamp-based partitioning
- Even distribution across numeric ranges
- Primary key index provides optimal query performance

## Configuration

Add the following settings to `appsettings.json`:

```json
{
  "Migration": {
    "Mode": "Embedding",
    "BatchSize": 1000,
    "MaxDegreeOfParallelism": 4,
    "ConcurrentBatchProcessors": 3,
    "MaxChannelCapacity": 10,
    "EnableCheckpoint": true,
    "CheckpointFilePath": "migration_checkpoint.json",
    "CheckpointInterval": 10,
    "ParallelMemberProducers": 3,
    "ParallelBundleProducers": 4
  }
}
```

### Configuration Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `ParallelMemberProducers` | 1 | Number of concurrent producers for reading members. Set to 1 to disable parallel queries. |
| `ParallelBundleProducers` | 1 | Number of concurrent producers for reading bundles. Set to 1 to disable parallel queries. |

**Note:** When set to 1, the system behaves exactly like the original single-producer implementation.

## How It Works

### Architecture

```
PostgreSQL                     Channel                    MongoDB
    |                             |                           |
    |---> Producer 1 (P1) ------->|                           |
    |     (Partition 1)           |                           |
    |                             |---> Consumer 1 ------->   |
    |---> Producer 2 (P2) ------->|     (Convert+Write)       |
    |     (Partition 2)           |                           |
    |                             |---> Consumer 2 ------->   |
    |---> Producer 3 (P3) ------->|     (Convert+Write)       |
    |     (Partition 3)           |                           |
    |                             |---> Consumer 3 ------->   |
    |                             |     (Convert+Write)       |
```

### Execution Flow

1. **Partition Creation Phase**
   - System queries min/max values for partitioning field
   - Calculates partition boundaries
   - Displays partition information in console

2. **Parallel Reading Phase**
   - Multiple producer tasks start simultaneously
   - Each producer reads from its assigned partition
   - Batches are written to a shared bounded channel
   - Batch numbers are synchronized across all producers

3. **Concurrent Processing Phase**
   - Multiple consumer workers read from the channel
   - Data conversion happens in parallel within each batch
   - Upsert operations to MongoDB using bulk writes
   - Progress tracking and checkpoint saving

4. **Completion Phase**
   - All producers complete their partitions
   - Channel is marked as complete
   - Consumers finish processing remaining batches
   - Final checkpoint saved (if enabled)

## Checkpoint Behavior

**Important:** Parallel query partitioning is **disabled when resuming from a checkpoint** to ensure data consistency and avoid duplicate processing.

- When starting fresh: Parallel producers are used (if configured)
- When resuming: Falls back to single producer with cursor pagination
- This ensures checkpoint `lastMemberId` or `lastBundleId` is respected correctly

## Performance Considerations

### Recommended Settings

#### High-Performance Systems (32GB+ RAM, 8+ CPU cores)
```json
{
  "BatchSize": 2000,
  "MaxDegreeOfParallelism": 8,
  "ConcurrentBatchProcessors": 5,
  "MaxChannelCapacity": 15,
  "ParallelMemberProducers": 4,
  "ParallelBundleProducers": 6
}
```

#### Balanced Systems (16GB RAM, 4-8 CPU cores)
```json
{
  "BatchSize": 1000,
  "MaxDegreeOfParallelism": 4,
  "ConcurrentBatchProcessors": 3,
  "MaxChannelCapacity": 10,
  "ParallelMemberProducers": 3,
  "ParallelBundleProducers": 4
}
```

#### Low-Resource Systems (8GB RAM, 2-4 CPU cores)
```json
{
  "BatchSize": 500,
  "MaxDegreeOfParallelism": 2,
  "ConcurrentBatchProcessors": 2,
  "MaxChannelCapacity": 5,
  "ParallelMemberProducers": 2,
  "ParallelBundleProducers": 2
}
```

### Tuning Guidelines

1. **Database Connection Limits**
   - Each producer uses a connection from the PostgreSQL connection pool
   - Ensure `MaxPoolSize` in connection string can accommodate: `ParallelProducers + ConcurrentBatchProcessors`
   - Monitor PostgreSQL connection count during migration

2. **Memory Usage**
   - More producers = more concurrent batches in memory
   - Monitor memory usage and adjust `MaxChannelCapacity` if needed
   - Each batch size affects memory footprint

3. **CPU Utilization**
   - Parallel producers increase I/O parallelism
   - `MaxDegreeOfParallelism` controls CPU-bound data conversion
   - Balance between database I/O and CPU processing

4. **Network Bandwidth**
   - More producers increase network traffic to/from databases
   - Ensure network can handle increased concurrent connections
   - Consider network latency in configuration

### Expected Performance Improvements

| Configuration | Expected Improvement |
|---------------|---------------------|
| Single producer (baseline) | 100% |
| 2 parallel producers | 140-170% |
| 3-4 parallel producers | 180-240% |
| 5+ parallel producers | 220-300%* |

*Actual improvements depend on:
- Database server capacity
- Network latency
- Hardware resources
- Data distribution

## Benefits

1. **Higher Database Throughput**
   - Multiple concurrent queries to PostgreSQL
   - Better utilization of database connection pool
   - Reduced idle time waiting for query results

2. **Improved Resource Utilization**
   - Maximizes database server CPU and I/O
   - Better network bandwidth utilization
   - Overlapping I/O and processing operations

3. **Scalability**
   - Easily scale by increasing parallel producers
   - Adapts to available hardware resources
   - Linear performance gains up to hardware limits

4. **Flexibility**
   - Can be enabled/disabled per entity type (members vs bundles)
   - Falls back gracefully to single producer when resuming
   - Compatible with all existing features (checkpoints, concurrent consumers)

## Limitations

1. **Checkpoint Resume**
   - Parallel producers are disabled when resuming from checkpoint
   - Single producer is used to respect checkpoint cursor position
   - This is by design to ensure data consistency

2. **Data Distribution**
   - Performance depends on even data distribution across partitions
   - Skewed data may cause some producers to finish earlier than others
   - Members partitioned by `update_at` may have uneven distribution if data was bulk-loaded

3. **Database Load**
   - Increases concurrent load on PostgreSQL
   - May impact other applications using the same database
   - Consider running during off-peak hours for production systems

4. **Connection Overhead**
   - More producers = more database connections required
   - Ensure PostgreSQL max_connections is configured appropriately
   - Connection pool exhaustion may cause failures

## Troubleshooting

### Issue: Producers Not Starting

**Symptom:** Only one producer appears to be working

**Solution:**
- Check configuration: Ensure `ParallelMemberProducers` > 1
- Verify not resuming from checkpoint (parallel is disabled on resume)
- Check logs for partition creation messages

### Issue: Out of Memory Errors

**Symptom:** Application crashes or slows down significantly

**Solution:**
- Reduce `MaxChannelCapacity` to limit queued batches
- Reduce `BatchSize` to decrease memory per batch
- Reduce number of parallel producers
- Increase available system memory

### Issue: Database Connection Errors

**Symptom:** "Too many connections" or connection pool exhausted errors

**Solution:**
- Increase PostgreSQL `max_connections` setting
- Increase connection pool `MaxPoolSize` in connection string
- Reduce `ParallelProducers + ConcurrentBatchProcessors`
- Monitor and tune based on actual connection usage

### Issue: Uneven Performance

**Symptom:** Some producers finish much faster than others

**Solution:**
- For members: Data may be unevenly distributed by `update_at`
  - This is expected for bulk-loaded data
  - Overall performance is still improved
- For bundles: ID-based partitioning should be more even
  - Check for large gaps in bundle IDs
  - Consider data distribution analysis

## Example Usage

### Enable Parallel Queries

Edit `appsettings.json`:

```json
{
  "Migration": {
    "ParallelMemberProducers": 3,
    "ParallelBundleProducers": 4
  }
}
```

Run migration:

```bash
dotnet run --project MemberServiceMigration
```

Expected output:

```
Creating 3 member partitions based on update_at range:
  Min update_at: 2024-01-01 00:00:00
  Max update_at: 2024-12-31 23:59:59
  Partition 0: 2024-01-01 00:00:00 to 2024-05-01 00:00:00
  Partition 1: 2024-05-01 00:00:00 to 2024-09-01 00:00:00
  Partition 2: 2024-09-01 00:00:00 to 2024-12-31 23:59:59

Using 3 parallel producers for members
[Member Batch 1] Processed 1000 members in 0.45s
[Member Batch 2] Processed 1000 members in 0.43s
[Member Batch 3] Processed 1000 members in 0.47s
...

Creating 4 bundle partitions based on id range:
  Min id: 1
  Max id: 20000000
  Partition 0: id 1 to 5000000
  Partition 1: id 5000001 to 10000000
  Partition 2: id 10000001 to 15000000
  Partition 3: id 15000001 to 20000000

Using 4 parallel producers for bundles
[Bundle Batch 1] Processed 1000 bundles in 0.38s
...
```

### Disable Parallel Queries

To use the original single-producer behavior:

```json
{
  "Migration": {
    "ParallelMemberProducers": 1,
    "ParallelBundleProducers": 1
  }
}
```

## Comparison with Previous Implementation

| Feature | Previous | Parallel Query |
|---------|----------|----------------|
| Member Reading | Single producer | 1-N producers (configurable) |
| Bundle Reading | Single producer | 1-N producers (configurable) |
| Partitioning Strategy | N/A | Members by `update_at`, Bundles by `id` |
| Checkpoint Resume | Supported | Supported (falls back to single producer) |
| Database Throughput | Limited by single query | N queries in parallel |
| Configuration | Simple | More options, flexible |
| Complexity | Lower | Slightly higher |

## Conclusion

Parallel query processing provides significant performance improvements for large-scale data migrations by maximizing database throughput through concurrent query execution. The feature is designed to be:

- **Optional**: Set producers to 1 to use original behavior
- **Flexible**: Configure per entity type (members vs bundles)
- **Safe**: Falls back to single producer when resuming from checkpoint
- **Scalable**: Adapts to available hardware resources

For production migrations, start with conservative settings and gradually increase parallelism while monitoring system resources and database performance.
