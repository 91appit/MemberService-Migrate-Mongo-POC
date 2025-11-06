# Concurrent Processing Enhancement (併行加速處理)

## Overview

This document describes the concurrent processing enhancement implemented to improve database and program resource utilization during migration.

## What Changed

### Version 1: Sequential Processing (Original)
The migration process was sequential:
1. Read a batch from PostgreSQL
2. Convert data in parallel (CPU-bound)
3. Write to MongoDB
4. Repeat

**Problem**: While converting or writing one batch, the program was idle in terms of I/O. Database connections and network bandwidth were underutilized.

### Version 2: Concurrent Pipeline Processing (First Enhancement)
The migration uses a **producer-consumer pattern** with concurrent processing:
1. **Single Producer**: Continuously reads batches from PostgreSQL asynchronously
2. **Multiple Consumers**: N workers process and write batches concurrently
3. **Pipeline**: While one batch is being written to MongoDB, the next batch can be read from PostgreSQL and converted

**Benefit**: Better resource utilization through overlapping I/O operations and parallel processing.

### Version 3: Multiple Concurrent Producers (Current - NEW!)
The migration now supports **multiple concurrent producers** reading from PostgreSQL:
1. **Multiple Producers**: M producers read different data ranges from PostgreSQL concurrently
2. **Multiple Consumers**: N workers process and write batches concurrently
3. **Enhanced Pipeline**: Multiple producers can saturate the channel faster, keeping consumers busy

**Benefits**:
- Even better PostgreSQL connection utilization
- Reduced I/O wait time through parallel queries
- Better throughput when PostgreSQL can handle concurrent connections
- Data correctness maintained through range-based partitioning

## Architecture

### System.Threading.Channels
We use `System.Threading.Channels` for efficient producer-consumer communication:
- **Bounded Channel**: Controls memory usage by limiting queue depth
- **Async Operations**: Non-blocking reads and writes
- **Multiple Producers & Consumers**: M producers and N consumers processing concurrently

### Processing Flow (with Concurrent Producers)

```
PostgreSQL                   Channel              MongoDB
    |                           |                    |
    |---> Producer 1 ---------> |                    |
    |     (Range 1)             |                    |
    |                           |---> Consumer 1 --->|
    |---> Producer 2 ---------> |    (Convert+Write) |
    |     (Range 2)             |                    |
    |                           |---> Consumer 2 --->|
    |---> Producer 3 ---------> |    (Convert+Write) |
    |     (Range 3)             |                    |
    |                           |---> Consumer 3 --->|
    |                           |    (Convert+Write) |
```

### Range-Based Partitioning

To ensure data correctness with multiple concurrent producers, we partition the data by ID ranges:

**For Bundles (Sequential Long IDs)**:
- Calculate min and max bundle IDs
- Divide the range evenly among producers
- Each producer handles: `id > startId AND id <= endId`
- Example with 3 producers and IDs 1-3000:
  - Producer 1: `id <= 1000`
  - Producer 2: `id > 1000 AND id <= 2000`
  - Producer 3: `id > 2000`

**For Members (UUID IDs)**:
- Sample member IDs at regular intervals using OFFSET
- Create partitions based on UUID ordering in PostgreSQL
- Each producer handles a specific UUID range
- Example with 3 producers:
  - Producer 1: `id <= <uuid_at_33%>`
  - Producer 2: `id > <uuid_at_33%> AND id <= <uuid_at_66%>`
  - Producer 3: `id > <uuid_at_66%>`

## Configuration

### New Settings in appsettings.json

```json
{
  "Migration": {
    "Mode": "Embedding",
    "BatchSize": 1000,
    "MaxDegreeOfParallelism": 4,
    "ConcurrentBatchProcessors": 3,
    "MaxChannelCapacity": 10,
    "ConcurrentProducers": 1
  }
}
```

### Configuration Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `BatchSize` | 1000 | Number of records per batch |
| `MaxDegreeOfParallelism` | 4 | Number of parallel threads for data conversion within each batch |
| `ConcurrentBatchProcessors` | 3 | Number of concurrent consumer workers processing batches |
| `MaxChannelCapacity` | 10 | Maximum number of batches queued in the channel |
| `ConcurrentProducers` | 1 | Number of concurrent producer tasks reading from PostgreSQL (NEW) |

### Tuning Recommendations

#### High-Performance Systems (32GB+ RAM, 8+ CPU cores)
```json
{
  "BatchSize": 2000,
  "MaxDegreeOfParallelism": 8,
  "ConcurrentBatchProcessors": 5,
  "MaxChannelCapacity": 15,
  "ConcurrentProducers": 3
}
```

#### Balanced Systems (16GB RAM, 4-8 CPU cores)
```json
{
  "BatchSize": 1000,
  "MaxDegreeOfParallelism": 4,
  "ConcurrentBatchProcessors": 3,
  "MaxChannelCapacity": 10,
  "ConcurrentProducers": 2
}
```

#### Low-Resource Systems (8GB RAM, 2-4 CPU cores)
```json
{
  "BatchSize": 500,
  "MaxDegreeOfParallelism": 2,
  "ConcurrentBatchProcessors": 2,
  "MaxChannelCapacity": 5,
  "ConcurrentProducers": 1
}
```

**Notes on ConcurrentProducers**:
- **Default**: 1 (single producer, same as before)
- **Recommended**: 2-3 for most systems
- **Maximum**: Depends on PostgreSQL's max_connections and load
- **Consideration**: Each producer creates a database connection
- **Best for**: Systems with fast network and PostgreSQL that can handle concurrent queries
- **Not recommended**: If PostgreSQL is already under heavy load or has connection limits

## How It Works

### Phase 1: Members Migration (Both Modes)

1. **Producer Task**:
   - Reads member batches from PostgreSQL using cursor pagination
   - Writes batches to the bounded channel
   - Completes the channel when all data is read

2. **Consumer Tasks** (N concurrent workers):
   - Read batches from the channel
   - Convert members to MongoDB documents in parallel
   - Write to MongoDB using unordered bulk inserts
   - Report progress

### Phase 2: Bundles Migration

**Referencing Mode**:
- Similar to members migration
- Reads bundles and writes to separate collection

**Embedding Mode**:
- Reads bundle batches
- Groups bundles by member ID
- Updates member documents using bulk operations

## Benefits

### 1. Better Resource Utilization
- **CPU**: Parallel data conversion + concurrent batch processing
- **Network**: Overlapping reads from PostgreSQL and writes to MongoDB + concurrent PostgreSQL queries
- **PostgreSQL**: Multiple concurrent queries maximize database throughput
- **Database Connections**: Multiple active queries from different producers instead of sequential

### 2. Reduced Idle Time
- While one batch is being written to MongoDB, the next batch is already being read and converted
- Multiple producers ensure the channel is always fed with data
- Consumers rarely wait for data when using multiple producers
- No waiting for I/O operations to complete

### 3. Improved Throughput
Expected improvements:
- **Sequential Baseline**: 100% (original performance)
- **Single Producer + Multiple Consumers**: 130-180% (30-80% faster)
- **Multiple Producers + Multiple Consumers**: 150-250% (50-150% faster) - NEW!
- Actual speedup depends on:
  - Network latency between app and databases
  - CPU cores available
  - Database load and response times
  - PostgreSQL's ability to handle concurrent queries
  - Number of concurrent producers configured

### 4. Scalability
- Easily adjust concurrency level based on hardware
- Can scale up with more powerful servers and databases
- Independent tuning of producers and consumers
- Graceful handling of errors and cancellation

## When to Use Concurrent Producers

### Use Multiple Producers (2-3) When:
✅ PostgreSQL has sufficient CPU and I/O capacity  
✅ Network bandwidth is high (1Gbps+)  
✅ PostgreSQL max_connections allows for extra connections  
✅ Migration needs to complete faster  
✅ System has adequate CPU cores (4+)

### Use Single Producer (1) When:
⚠️ PostgreSQL is already under heavy load  
⚠️ Network latency is high or bandwidth is limited  
⚠️ PostgreSQL connection pool is limited  
⚠️ System has limited resources  
⚠️ Want to minimize PostgreSQL impact during business hours
- **Database Connections**: Multiple active queries instead of sequential

### 2. Reduced Idle Time
- While one batch is being written to MongoDB, the next batch is already being read and converted
- No waiting for I/O operations to complete

### 3. Improved Throughput
Expected improvements:
- **Sequential Baseline**: 100% (original performance)
- **With Concurrent Processing**: 130-180% (30-80% faster)
- Actual speedup depends on:
  - Network latency between app and databases
  - CPU cores available
  - Database load and response times

### 4. Scalability
- Easily adjust concurrency level based on hardware
- Can scale up with more powerful servers
- Graceful handling of errors and cancellation

## Error Handling

### Cancellation Support
- All async operations support cancellation tokens
- If one consumer fails, all consumers are cancelled
- Proper cleanup of resources

### Exception Handling
- Producer errors complete the channel with exception
- Consumer errors trigger cancellation
- Detailed error messages for troubleshooting

## Monitoring

### Progress Reporting
The output now shows:
```
[Member Batch 15] Processed 1000 members in 0.45s
Progress: 15000/5000000 members (0.30%) - Est. remaining: 01:23:45
```

### Key Metrics
- **Batch Number**: Sequential batch identifier
- **Batch Time**: Time to process one batch
- **Progress**: Current count and percentage
- **Estimated Remaining Time**: Based on average batch time

## Technical Details

### Thread Safety
- `ConcurrentBag` for parallel data conversion
- `lock` statement for progress tracking
- Thread-safe channel operations

### Memory Management
- Bounded channel prevents memory exhaustion
- Batch processing ensures constant memory footprint
- Proper disposal of resources

### Async/Await Pattern
- Efficient use of async/await throughout
- Non-blocking I/O operations
- Task-based parallelism

## Comparison with Previous Optimizations

| Optimization | Type | Impact | Concurrent Processing |
|--------------|------|--------|----------------------|
| Deferred Index Creation | I/O | High | ✓ Still active |
| Unordered Bulk Inserts | I/O | High | ✓ Still active |
| Connection Pooling | I/O | Medium | ✓ Still active |
| Parallel Conversion | CPU | Medium | ✓ Enhanced |
| **Concurrent Pipeline** | **I/O + CPU** | **Medium-High** | **NEW** |

## Best Practices

### 1. Start Conservative
Begin with default settings and monitor performance:
```json
{
  "ConcurrentBatchProcessors": 3,
  "MaxChannelCapacity": 10
}
```

### 2. Monitor Resource Usage
- Watch CPU utilization (should be 60-80%)
- Monitor memory usage (should remain stable)
- Check database connection counts

### 3. Adjust Based on Bottlenecks
- If CPU is idle → Increase `ConcurrentBatchProcessors` or `MaxDegreeOfParallelism`
- If memory is high → Decrease `MaxChannelCapacity` or `BatchSize`
- If database is overloaded → Decrease `ConcurrentBatchProcessors`

### 4. Test Before Production
Run test migrations with different configurations to find optimal settings for your environment.

## Limitations

1. **Database Constraints**: If PostgreSQL or MongoDB cannot handle concurrent connections, performance may degrade
2. **Network Bandwidth**: Limited bandwidth can bottleneck concurrent operations
3. **Memory**: More concurrent processing requires more memory
4. **Coordination Overhead**: Too many concurrent processors can cause contention

## Future Enhancements

Potential improvements:
1. Dynamic adjustment of concurrency based on system load
2. Separate producer/consumer configurations for reads vs writes
3. Priority queue for handling different data types
4. Metrics dashboard for real-time monitoring
5. Adaptive batch sizing based on performance

## Conclusion

The concurrent processing enhancement provides significant performance improvements by:
- Overlapping I/O operations
- Maximizing CPU and network utilization
- Maintaining low and predictable memory usage
- Providing flexible configuration options

This makes the migration tool more efficient and scalable for large-scale data migrations.
