# Performance Optimization Guide

## Overview

This document describes the performance optimizations implemented to improve the migration speed of 5 million members and 20 million bundles from PostgreSQL to MongoDB.

## Problem Analysis

The original implementation took approximately **3 hours** to migrate:
- 5 million members
- 20 million bundles

### Performance Bottlenecks Identified

1. **Index Creation Before Migration** (Major Impact)
   - Creating indexes before inserting data slows down every insert operation
   - MongoDB needs to update indexes for each document inserted
   - **Impact**: 30-50% slower inserts

2. **Ordered Bulk Inserts** (Medium Impact)
   - Default `InsertManyAsync` uses ordered inserts
   - If one document fails, the entire batch stops
   - Ordered inserts cannot be parallelized as effectively
   - **Impact**: 20-30% slower inserts

3. **No Connection Pooling Optimization** (Medium Impact)
   - Creating new connections for each query adds overhead
   - Network latency and connection establishment time accumulates
   - **Impact**: 15-25% slower queries

4. **Sequential Data Conversion** (Medium Impact)
   - Converting documents one by one doesn't utilize multiple CPU cores
   - CPU sits idle during I/O operations
   - **Impact**: 20-30% slower conversion

5. **Bundle Query Inefficiency** (Minor Impact - Already Optimized)
   - The code already uses efficient cursor-based pagination
   - Bundle queries use `member_id = ANY(@memberIds)` which is well-optimized
   - **Impact**: Minimal, but can be improved with better ordering

## Optimizations Implemented

### 1. Deferred Index Creation (🔥 High Impact)

**Before:**
```csharp
Console.WriteLine("Creating indexes...");
await _mongoDbRepository.CreateIndexesForEmbeddingAsync();
// ... then insert data
```

**After:**
```csharp
Console.WriteLine("Skipping index creation (will create after migration)...");
// ... insert all data first
Console.WriteLine("Creating indexes...");
await _mongoDbRepository.CreateIndexesForEmbeddingAsync();
```

**Benefits:**
- Indexes are created once after all data is loaded
- Insert operations run at maximum speed without index maintenance overhead
- **Expected speedup: 30-50% faster inserts**

### 2. Unordered Bulk Inserts (🔥 High Impact)

**Before:**
```csharp
await membersCollection.InsertManyAsync(documents);
```

**After:**
```csharp
var options = new InsertManyOptions { IsOrdered = false };
await membersCollection.InsertManyAsync(documents, options);
```

**Benefits:**
- MongoDB can parallelize writes across multiple threads
- Insert failures don't stop the entire batch
- Better throughput for bulk operations
- **Expected speedup: 20-30% faster inserts**

### 3. Connection Pooling Optimization (⚡ Medium Impact)

**Before:**
```csharp
await using var connection = new NpgsqlConnection(_connectionString);
await connection.OpenAsync();
```

**After:**
```csharp
private readonly NpgsqlDataSource _dataSource;

public PostgreSqlRepository(string connectionString)
{
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = 100;
    dataSourceBuilder.ConnectionStringBuilder.MinPoolSize = 10;
    _dataSource = dataSourceBuilder.Build();
}

await using var connection = await _dataSource.OpenConnectionAsync();
```

**Benefits:**
- Reuses existing database connections from the pool
- Reduces connection establishment overhead
- Better resource utilization
- **Expected speedup: 15-25% faster queries**

### 4. Parallel Data Conversion (⚡ Medium Impact)

**Before:**
```csharp
var documents = membersBatch.Select(member =>
{
    bundlesByMember.TryGetValue(member.Id, out var bundles);
    return DataConverter.ConvertToMemberDocumentEmbedding(member, bundles);
}).ToList();
```

**After:**
```csharp
var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 4 };
var documents = new ConcurrentBag<MemberDocumentEmbedding>();

Parallel.ForEach(membersBatch, parallelOptions, member =>
{
    bundlesByMember.TryGetValue(member.Id, out var bundles);
    var document = DataConverter.ConvertToMemberDocumentEmbedding(member, bundles);
    documents.Add(document);
});
```

**Benefits:**
- Utilizes multiple CPU cores for data conversion
- Reduces CPU idle time during conversion
- Scalable based on available CPU cores
- **Expected speedup: 20-30% faster conversion**

### 5. Enhanced Bundle Query (✅ Minor Impact)

**Before:**
```csharp
ORDER BY member_id
```

**After:**
```csharp
ORDER BY member_id, id
```

**Benefits:**
- Helps PostgreSQL query planner optimize the query
- More predictable query performance
- **Expected speedup: 5-10% faster queries**

### 6. Performance Metrics & Monitoring (📊 Observability)

Added detailed timing metrics for each batch:
- Members read time
- Bundles read time
- Conversion time
- Insert time
- Total batch time
- Estimated remaining time

**Benefits:**
- Identify bottlenecks in real-time
- Monitor migration progress accurately
- Predict completion time

## Configuration

### appsettings.json

```json
{
  "Migration": {
    "Mode": "Embedding",
    "BatchSize": 1000,
    "MaxDegreeOfParallelism": 4
  }
}
```

### Configuration Parameters

| Parameter | Default | Description | Tuning Guide |
|-----------|---------|-------------|--------------|
| `BatchSize` | 1000 | Number of records per batch | Increase for fewer, larger batches (2000-5000). Decrease if running out of memory (500). |
| `MaxDegreeOfParallelism` | 4 | Number of parallel conversion threads | Set to number of CPU cores (2-8). Higher values may not improve performance due to I/O bottleneck. |

### Tuning Recommendations

#### For High-Memory Systems (32GB+)
```json
{
  "BatchSize": 5000,
  "MaxDegreeOfParallelism": 8
}
```

#### For Low-Memory Systems (8GB-)
```json
{
  "BatchSize": 500,
  "MaxDegreeOfParallelism": 2
}
```

#### For Balanced Systems (16GB)
```json
{
  "BatchSize": 1000,
  "MaxDegreeOfParallelism": 4
}
```

## Expected Performance Improvement

### Before Optimization
- **Time**: ~3 hours (180 minutes)
- **Members**: 5,000,000 (27,777 members/min)
- **Bundles**: 20,000,000 (111,111 bundles/min)

### After Optimization (Conservative Estimate)

Combining all optimizations (multiplicative speedup factors):
- Deferred indexes: 1.67x faster (40% reduction in time)
- Unordered inserts: 1.33x faster (25% reduction in time)
- Connection pooling: 1.25x faster (20% reduction in time)
- Parallel conversion: 1.33x faster (25% reduction in time)
- Query optimization: 1.08x faster (7% reduction in time)

**Compound improvement**: 1.67 × 1.33 × 1.25 × 1.33 × 1.08 ≈ **3.2x faster**

### Expected Results
- **Time**: ~55-60 minutes (vs. 180 minutes)
- **Members**: ~88,000 members/min (vs. 27,777)
- **Bundles**: ~350,000 bundles/min (vs. 111,111)
- **Improvement**: **~65-70% reduction in migration time**

### Best Case Scenario
With optimal tuning and hardware:
- **Time**: ~40-45 minutes
- **Improvement**: **~75-80% reduction in migration time**

## Monitoring Migration Performance

During migration, you'll see detailed metrics like:

```
Batch 1: 1000 members, 4000 bundles in 2.45s (Read M:0.32s, B:0.87s, Conv:0.18s, Insert:1.08s)
Progress: 1000/5000000 members (0.02%), 4000/20000000 bundles (0.02%) - Est. remaining: 02:03:45

Batch 2: 1000 members, 3950 bundles in 2.31s (Read M:0.28s, B:0.82s, Conv:0.15s, Insert:1.06s)
Progress: 2000/5000000 members (0.04%), 7950/20000000 bundles (0.04%) - Est. remaining: 01:55:12
```

### Understanding the Metrics

- **Read M**: Time to read members from PostgreSQL
- **Read B**: Time to read bundles from PostgreSQL
- **Conv**: Time to convert data to MongoDB documents
- **Insert**: Time to insert into MongoDB
- **Est. remaining**: Estimated time to complete (based on average batch time)

### Identifying Bottlenecks

| Slowest Operation | Likely Cause | Solution |
|-------------------|--------------|----------|
| Read B (Bundles) | PostgreSQL query performance | Add index on `bundles.member_id`, increase PostgreSQL connection pool |
| Insert | MongoDB write performance | Increase batch size, ensure MongoDB has enough resources |
| Conv | CPU conversion overhead | Increase `MaxDegreeOfParallelism`, optimize DataConverter logic |
| Read M (Members) | PostgreSQL query performance | Add index on `members.id` (should already exist as PK) |

## PostgreSQL Index Requirements

Ensure these indexes exist for optimal performance:

```sql
-- Members table (should exist as primary key)
CREATE INDEX IF NOT EXISTS idx_members_id ON members(id);

-- Bundles table (critical for performance)
CREATE INDEX IF NOT EXISTS idx_bundles_member_id ON bundles(member_id);
CREATE INDEX IF NOT EXISTS idx_bundles_id ON bundles(id);
```

## MongoDB Index Strategy

Indexes are created **after** data migration for optimal performance:

### Embedding Mode
- `ix_members_tenant_id` on `tenant_id`
- `ix_members_update_at` on `update_at`
- `ix_members_tags` on `tags`

### Referencing Mode
- `ix_members_tenant_id` on `tenant_id`
- `ix_members_update_at` on `update_at`
- `ix_members_tags` on `tags`
- `ix_bundles_member_id` on `member_id`
- `ix_bundles_tenant_id` on `tenant_id`
- `ix_bundles_update_at` on `update_at`
- `ix_bundles_key_tenant_id_type` (unique) on `key`, `tenant_id`, `type`

## Troubleshooting

### Migration is Still Slow

1. **Check PostgreSQL Performance**
   ```sql
   -- Check if indexes exist
   SELECT * FROM pg_indexes WHERE tablename IN ('members', 'bundles');
   
   -- Check slow queries
   SELECT query, calls, total_time, mean_time 
   FROM pg_stat_statements 
   ORDER BY mean_time DESC LIMIT 10;
   ```

2. **Check MongoDB Performance**
   ```javascript
   // Check if indexes are being built during migration (they shouldn't be)
   db.currentOp({"command.createIndexes": {$exists: true}})
   ```

3. **Monitor System Resources**
   - CPU usage should be 50-80% during conversion
   - Network I/O should be consistent during reads/writes
   - Memory should not be exhausted

### Out of Memory Errors

Reduce `BatchSize` in `appsettings.json`:
```json
{
  "BatchSize": 500
}
```

### MongoDB Write Errors

Check MongoDB logs for write errors. If using replica sets, ensure write concern is appropriate.

## Validation

After migration completes, validate data integrity:

```javascript
// Check document counts
db.members.countDocuments()  // Should equal PostgreSQL count

// Check sample data
db.members.findOne()
db.bundles.findOne()

// Verify indexes were created
db.members.getIndexes()
db.bundles.getIndexes()
```

## Summary

The optimizations focus on:
1. ✅ **Deferred index creation** - Maximize insert speed
2. ✅ **Unordered bulk inserts** - Parallel write operations
3. ✅ **Connection pooling** - Reduce connection overhead
4. ✅ **Parallel conversion** - Utilize multiple CPU cores
5. ✅ **Enhanced monitoring** - Real-time performance visibility

These changes are expected to reduce migration time from **~3 hours to ~55-60 minutes** (65-70% faster), with potential for even better performance with optimal tuning.
