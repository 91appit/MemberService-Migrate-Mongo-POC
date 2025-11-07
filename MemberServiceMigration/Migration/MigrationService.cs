using MemberServiceMigration.Configuration;
using MemberServiceMigration.Database;
using MongoDB.Driver;
using System.Threading.Channels;

namespace MemberServiceMigration.Migration;

// Helper classes for concurrent processing
internal class MemberBatch
{
    public List<Models.Member> Members { get; set; } = new();
    public Guid? LastMemberId { get; set; }
    public int BatchNumber { get; set; }
    public int ProducerId { get; set; }
}

internal class BundleBatch
{
    public List<Models.Bundle> Bundles { get; set; } = new();
    public long? LastBundleId { get; set; }
    public int BatchNumber { get; set; }
    public int ProducerId { get; set; }
}

// Helper class for member query partition
internal class MemberPartition
{
    public int PartitionId { get; set; }
    public DateTime StartUpdateAt { get; set; }
    public DateTime EndUpdateAt { get; set; }
}

// Helper class for bundle query partition
internal class BundlePartition
{
    public int PartitionId { get; set; }
    public long StartId { get; set; }
    public long EndId { get; set; }
}

public class MigrationService
{
    private readonly PostgreSqlRepository _postgreSqlRepository;
    private readonly MongoDbRepository _mongoDbRepository;
    private readonly MigrationSettings _settings;
    private readonly CheckpointService? _checkpointService;

    public MigrationService(
        PostgreSqlRepository postgreSqlRepository,
        MongoDbRepository mongoDbRepository,
        MigrationSettings settings)
    {
        _postgreSqlRepository = postgreSqlRepository;
        _mongoDbRepository = mongoDbRepository;
        _settings = settings;
        
        if (_settings.EnableCheckpoint)
        {
            _checkpointService = new CheckpointService(_settings.CheckpointFilePath);
        }
    }

    private static void LogException(Exception ex, string context)
    {
        Console.WriteLine($"{context}: {ex.Message}");
        if (ex.InnerException != null)
        {
            Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
        }
        Console.WriteLine($"Full exception details:\n{ex}");
    }

    public async Task MigrateAsync()
    {
        Console.WriteLine($"Starting migration in {_settings.Mode} mode...");
        Console.WriteLine($"Batch size: {_settings.BatchSize}");
        Console.WriteLine($"Checkpoint enabled: {_settings.EnableCheckpoint}");

        MigrationCheckpoint? existingCheckpoint = null;
        
        if (_checkpointService != null)
        {
            existingCheckpoint = await _checkpointService.LoadCheckpointAsync();
            
            if (existingCheckpoint != null)
            {
                Console.WriteLine();
                Console.WriteLine("=== Existing Checkpoint Found ===");
                Console.WriteLine($"Mode: {existingCheckpoint.Mode}");
                Console.WriteLine($"Last Member ID: {existingCheckpoint.LastMemberId?.ToString() ?? "N/A"}");
                Console.WriteLine($"Last Bundle ID: {existingCheckpoint.LastBundleId?.ToString() ?? "N/A"}");
                Console.WriteLine($"Timestamp: {existingCheckpoint.Timestamp:yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"Status: {existingCheckpoint.Status}");
                Console.WriteLine();
                
                if (existingCheckpoint.Mode != _settings.Mode)
                {
                    Console.WriteLine($"WARNING: Checkpoint mode ({existingCheckpoint.Mode}) differs from current mode ({_settings.Mode})");
                }
                
                Console.Write("Do you want to resume from the checkpoint? (Y/N, default: Y): ");
                var response = Console.ReadLine()?.Trim().ToUpperInvariant();
                
                if (response == "N" || response == "NO")
                {
                    Console.WriteLine("Starting fresh migration. Deleting checkpoint...");
                    _checkpointService.DeleteCheckpoint();
                    existingCheckpoint = null;
                }
                else
                {
                    Console.WriteLine("Resuming migration from checkpoint...");
                }
                Console.WriteLine();
            }
        }

        try
        {
            if (_settings.Mode == MigrationMode.Embedding)
            {
                await MigrateEmbeddingModeAsync(existingCheckpoint);
            }
            else
            {
                await MigrateReferencingModeAsync(existingCheckpoint);
            }

            Console.WriteLine("Migration completed successfully!");
            
            // Clear checkpoint on successful completion
            if (_checkpointService != null)
            {
                _checkpointService.DeleteCheckpoint();
                Console.WriteLine("Checkpoint cleared.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Migration failed: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            
            if (_checkpointService != null && _settings.EnableCheckpoint)
            {
                Console.WriteLine($"Checkpoint saved. You can resume the migration by running again.");
            }
            
            throw;
        }
    }

    private async Task<List<MemberPartition>> CreateMemberPartitionsAsync()
    {
        var partitions = new List<MemberPartition>();
        
        if (_settings.ParallelMemberProducers <= 1)
        {
            // No partitioning needed for single producer
            return partitions;
        }

        var (minUpdateAt, maxUpdateAt) = await _postgreSqlRepository.GetMembersUpdateAtRangeAsync();

        Console.WriteLine($"Creating member partitions based on update_at range:");
        Console.WriteLine($"  Min update_at: {minUpdateAt:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"  Max update_at: {maxUpdateAt:yyyy-MM-dd HH:mm:ss}");

        // Check if custom partition boundaries are configured
        if (_settings.MemberPartitionBoundaries != null && _settings.MemberPartitionBoundaries.Any())
        {
            // Use custom boundaries for data-driven partitioning
            Console.WriteLine($"Using {_settings.MemberPartitionBoundaries.Count + 1} custom partition boundaries");
            
            var boundaries = new List<DateTime>();
            foreach (var boundaryStr in _settings.MemberPartitionBoundaries)
            {
                if (DateTime.TryParse(boundaryStr, out var boundary))
                {
                    boundaries.Add(boundary);
                }
                else
                {
                    throw new InvalidOperationException($"Invalid date format in MemberPartitionBoundaries: {boundaryStr}");
                }
            }
            
            // Sort boundaries to ensure proper ordering
            boundaries.Sort();
            
            // Create partitions based on custom boundaries
            var partitionTasks = new List<Task>();
            for (int i = 0; i < boundaries.Count + 1; i++)
            {
                DateTime startUpdateAt;
                DateTime endUpdateAt;
                
                if (i == 0)
                {
                    // First partition: min to first boundary
                    startUpdateAt = minUpdateAt;
                    endUpdateAt = boundaries[0];
                }
                else if (i == boundaries.Count)
                {
                    // Last partition: last boundary to max
                    startUpdateAt = boundaries[i - 1];
                    endUpdateAt = maxUpdateAt.AddSeconds(1); // Include the last record
                }
                else
                {
                    // Middle partitions: boundary[i-1] to boundary[i]
                    startUpdateAt = boundaries[i - 1];
                    endUpdateAt = boundaries[i];
                }
                
                var partition = new MemberPartition
                {
                    PartitionId = i,
                    StartUpdateAt = startUpdateAt,
                    EndUpdateAt = endUpdateAt
                };
                partitions.Add(partition);
            }
            
            // Get counts for all partitions in parallel to show data distribution
            var countTasks = partitions.Select(async p =>
            {
                var count = await _postgreSqlRepository.GetMembersCountByUpdateAtRangeAsync(p.StartUpdateAt, p.EndUpdateAt);
                return (p.PartitionId, count);
            }).ToList();
            
            var counts = await Task.WhenAll(countTasks);
            
            // Display partition information with counts
            foreach (var (partitionId, count) in counts.OrderBy(c => c.Item1))
            {
                var partition = partitions[partitionId];
                Console.WriteLine($"  Partition {partitionId}: {partition.StartUpdateAt:yyyy-MM-dd HH:mm:ss} to {partition.EndUpdateAt:yyyy-MM-dd HH:mm:ss} (~{count:N0} members)");
            }
        }
        else
        {
            // Use time-based even partitioning (original behavior)
            Console.WriteLine($"Using {_settings.ParallelMemberProducers} evenly divided time-based partitions");
            
            var totalTimeSpan = maxUpdateAt - minUpdateAt;
            var partitionTimeSpan = TimeSpan.FromTicks(totalTimeSpan.Ticks / _settings.ParallelMemberProducers);

            for (int i = 0; i < _settings.ParallelMemberProducers; i++)
            {
                var startUpdateAt = minUpdateAt.Add(TimeSpan.FromTicks(partitionTimeSpan.Ticks * i));
                var endUpdateAt = (i == _settings.ParallelMemberProducers - 1) 
                    ? maxUpdateAt.AddSeconds(1) // Include the last record
                    : minUpdateAt.Add(TimeSpan.FromTicks(partitionTimeSpan.Ticks * (i + 1)));

                var partition = new MemberPartition
                {
                    PartitionId = i,
                    StartUpdateAt = startUpdateAt,
                    EndUpdateAt = endUpdateAt
                };
                partitions.Add(partition);
                
                Console.WriteLine($"  Partition {i}: {startUpdateAt:yyyy-MM-dd HH:mm:ss} to {endUpdateAt:yyyy-MM-dd HH:mm:ss}");
            }
        }

        return partitions;
    }

    private async Task<List<BundlePartition>> CreateBundlePartitionsAsync()
    {
        var partitions = new List<BundlePartition>();
        
        if (_settings.ParallelBundleProducers <= 1)
        {
            // No partitioning needed for single producer
            return partitions;
        }

        var (minId, maxId) = await _postgreSqlRepository.GetBundlesIdRangeAsync();
        var totalRange = maxId - minId + 1;
        var partitionSize = totalRange / _settings.ParallelBundleProducers;

        Console.WriteLine($"Creating {_settings.ParallelBundleProducers} bundle partitions based on id range:");
        Console.WriteLine($"  Min id: {minId}");
        Console.WriteLine($"  Max id: {maxId}");

        for (int i = 0; i < _settings.ParallelBundleProducers; i++)
        {
            var startId = minId + (partitionSize * i);
            var endId = (i == _settings.ParallelBundleProducers - 1) 
                ? maxId + 1 // Include the last record
                : minId + (partitionSize * (i + 1));

            var partition = new BundlePartition
            {
                PartitionId = i,
                StartId = startId,
                EndId = endId
            };
            partitions.Add(partition);
            
            Console.WriteLine($"  Partition {i}: id {startId} to {endId - 1}");
        }

        return partitions;
    }

    private async Task MigrateEmbeddingModeAsync(MigrationCheckpoint? checkpoint)
    {
        Console.WriteLine("Counting records in PostgreSQL...");
        
        var totalMembers = await _postgreSqlRepository.GetMembersCountAsync();
        var totalBundles = await _postgreSqlRepository.GetBundlesCountAsync();
        Console.WriteLine($"Found {totalMembers} members and {totalBundles} bundles to migrate");

        var membersCollection = _mongoDbRepository.GetMembersEmbeddingCollection();
        
        // Check if collection already has data
        var existingCount = await membersCollection.CountDocumentsAsync(FilterDefinition<Models.MongoDB.MemberDocumentEmbedding>.Empty);
        
        // Only drop if not resuming from checkpoint
        if (existingCount > 0 && checkpoint == null)
        {
            Console.WriteLine($"WARNING: MongoDB collection already contains {existingCount} documents!");
            Console.WriteLine("Dropping existing collection to avoid duplicate key errors...");
            await _mongoDbRepository.DropMembersEmbeddingCollectionAsync();
            Console.WriteLine("Collection dropped successfully.");
            // Re-get the collection reference after dropping
            membersCollection = _mongoDbRepository.GetMembersEmbeddingCollection();
        }
        else if (existingCount > 0 && checkpoint != null)
        {
            Console.WriteLine($"Resuming migration. MongoDB collection contains {existingCount} documents.");
        }
        
        Console.WriteLine("Skipping index creation (will create after migration for better performance)...");

        var startTime = DateTime.UtcNow;
        var lastMemberId = checkpoint?.LastMemberId;
        var lastBundleId = checkpoint?.LastBundleId;

        // Phase 1: Migrate all members first (without bundles for better performance)
        Console.WriteLine($"Phase 1: Migrating members without bundles using {_settings.ConcurrentBatchProcessors} concurrent processors...");
        if (lastMemberId.HasValue)
        {
            Console.WriteLine($"Resuming from Member ID: {lastMemberId}");
        }
        
        var processedMemberCount = await MigrateMembersWithoutBundlesConcurrentlyAsync(membersCollection, totalMembers, lastMemberId);
        
        var membersTime = (DateTime.UtcNow - startTime).TotalSeconds;
        Console.WriteLine($"Phase 1 completed in {TimeSpan.FromSeconds(membersTime):hh\\:mm\\:ss}: {processedMemberCount} members migrated");

        // Phase 2: Migrate bundles and update member documents
        Console.WriteLine($"Phase 2: Migrating bundles and updating member documents using {_settings.ConcurrentBatchProcessors} concurrent processors...");
        if (lastBundleId.HasValue)
        {
            Console.WriteLine($"Resuming from Bundle ID: {lastBundleId}");
        }
        
        var bundlesStartTime = DateTime.UtcNow;
        var processedBundleCount = await MigrateBundlesAndUpdateMembersConcurrentlyAsync(membersCollection, totalBundles, lastBundleId);
        
        var bundlesTime = (DateTime.UtcNow - bundlesStartTime).TotalSeconds;
        Console.WriteLine($"Phase 2 completed in {TimeSpan.FromSeconds(bundlesTime):hh\\:mm\\:ss}: {processedBundleCount} bundles migrated");
        
        Console.WriteLine("Creating indexes...");
        var indexStartTime = DateTime.UtcNow;
        await _mongoDbRepository.CreateIndexesForEmbeddingAsync();
        var indexTime = (DateTime.UtcNow - indexStartTime).TotalSeconds;
        Console.WriteLine($"Indexes created in {TimeSpan.FromSeconds(indexTime):hh\\:mm\\:ss}");
        
        var totalTime = (DateTime.UtcNow - startTime).TotalSeconds;
        Console.WriteLine($"Total migration time: {TimeSpan.FromSeconds(totalTime):hh\\:mm\\:ss}");
    }

    private async Task<int> MigrateMembersWithoutBundlesConcurrentlyAsync(
        IMongoCollection<Models.MongoDB.MemberDocumentEmbedding> collection,
        long totalCount,
        Guid? startFromId = null)
    {
        var processedCount = 0;
        var startTime = DateTime.UtcNow;
        var cancellationTokenSource = new CancellationTokenSource();
        
        // Create a bounded channel for producer-consumer pattern
        var channel = Channel.CreateBounded<MemberBatch>(new BoundedChannelOptions(_settings.MaxChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        // Create partitions if parallel producers are enabled
        var memberPartitions = await CreateMemberPartitionsAsync();
        var usePartitioning = memberPartitions.Count > 0;
        
        if (usePartitioning)
        {
            Console.WriteLine($"Using {_settings.ParallelMemberProducers} parallel producers for members");
        }

        // Producer tasks: Read batches from PostgreSQL (one or multiple)
        var producerTasks = new List<Task>();
        var batchNumberCounter = 0;
        var batchNumberLock = new object();
        
        if (usePartitioning && startFromId == null)
        {
            // Parallel producers with partitioning (only when not resuming from checkpoint)
            foreach (var partition in memberPartitions)
            {
                var producerTask = Task.Run(async () =>
                {
                    try
                    {
                        Guid? lastMemberId = null;
                        
                        while (!cancellationTokenSource.Token.IsCancellationRequested)
                        {
                            var membersBatch = await _postgreSqlRepository.GetMembersBatchByUpdateAtRangeAsync(
                                partition.StartUpdateAt, partition.EndUpdateAt, lastMemberId, _settings.BatchSize);
                            
                            if (!membersBatch.Any())
                            {
                                break;
                            }
                            
                            int currentBatchNumber;
                            lock (batchNumberLock)
                            {
                                currentBatchNumber = ++batchNumberCounter;
                            }
                            
                            await channel.Writer.WriteAsync(new MemberBatch
                            {
                                Members = membersBatch,
                                LastMemberId = membersBatch.Last().Id,
                                BatchNumber = currentBatchNumber,
                                ProducerId = partition.PartitionId
                            });
                            
                            lastMemberId = membersBatch.Last().Id;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogException(ex, $"Producer {partition.PartitionId} error while reading members batch (embedding mode)");
                        throw;
                    }
                }, cancellationTokenSource.Token);
                
                producerTasks.Add(producerTask);
            }
            
            // Wait for all producers and complete the channel
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(producerTasks);
                    channel.Writer.Complete();
                }
                catch (Exception ex)
                {
                    channel.Writer.Complete(ex);
                    throw;
                }
            });
        }
        else
        {
            // Single producer (original behavior or when resuming from checkpoint)
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    Guid? lastMemberId = startFromId;
                    var currentBatchNumber = 0;
                    
                    while (!cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        var membersBatch = await _postgreSqlRepository.GetMembersBatchAsync(lastMemberId, _settings.BatchSize);
                        
                        if (!membersBatch.Any())
                        {
                            break;
                        }
                        
                        await channel.Writer.WriteAsync(new MemberBatch
                        {
                            Members = membersBatch,
                            LastMemberId = membersBatch.Last().Id,
                            BatchNumber = ++currentBatchNumber,
                            ProducerId = 0
                        });
                        
                        lastMemberId = membersBatch.Last().Id;
                    }
                    
                    channel.Writer.Complete();
                }
                catch (Exception ex)
                {
                    LogException(ex, "Producer error while reading members batch (embedding mode)");
                    channel.Writer.Complete(ex);
                    throw;
                }
            }, cancellationTokenSource.Token);
            
            producerTasks.Add(producerTask);
        }

        // Consumer tasks: Process and write batches to MongoDB
        var consumerTasks = new List<Task>();
        var lockObject = new object();
        Guid? lastProcessedMemberId = startFromId;
        
        for (int i = 0; i < _settings.ConcurrentBatchProcessors; i++)
        {
            var consumerTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var batch in channel.Reader.ReadAllAsync(cancellationTokenSource.Token))
                    {
                        var batchStartTime = DateTime.UtcNow;
                        
                        // Convert members to documents without bundles in parallel
                        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _settings.MaxDegreeOfParallelism };
                        var documents = new System.Collections.Concurrent.ConcurrentBag<Models.MongoDB.MemberDocumentEmbedding>();
                        
                        Parallel.ForEach(batch.Members, parallelOptions, member =>
                        {
                            var document = DataConverter.ConvertToMemberDocumentEmbedding(member, null);
                            documents.Add(document);
                        });

                        var documentsList = documents.ToList();
                        if (documentsList.Any())
                        {
                            // Use ReplaceOne with upsert to handle potential duplicates when resuming from checkpoint
                            var bulkOps = documentsList.Select(doc => 
                                new ReplaceOneModel<Models.MongoDB.MemberDocumentEmbedding>(
                                    Builders<Models.MongoDB.MemberDocumentEmbedding>.Filter.Eq(m => m.Id, doc.Id),
                                    doc)
                                {
                                    IsUpsert = true
                                });
                            
                            await collection.BulkWriteAsync(bulkOps, new BulkWriteOptions { IsOrdered = false }, cancellationTokenSource.Token);
                            
                            var batchTime = (DateTime.UtcNow - batchStartTime).TotalSeconds;
                            
                            lock (lockObject)
                            {
                                processedCount += documentsList.Count;
                                lastProcessedMemberId = batch.LastMemberId;
                                
                                var elapsedTime = (DateTime.UtcNow - startTime).TotalSeconds;
                                var avgTimePerRecord = processedCount > 0 ? elapsedTime / processedCount : 0;
                                var estimatedRemainingTime = avgTimePerRecord * (totalCount - processedCount);
                                
                                Console.WriteLine($"[Member Batch {batch.BatchNumber}] Processed {batch.Members.Count} members in {batchTime:F2}s");
                                Console.WriteLine($"Progress: {processedCount}/{totalCount} members ({(processedCount * 100.0 / totalCount):F2}%) - Est. remaining: {TimeSpan.FromSeconds(estimatedRemainingTime):hh\\:mm\\:ss}");
                                
                                // Save checkpoint periodically
                                if (_checkpointService != null && batch.BatchNumber % _settings.CheckpointInterval == 0)
                                {
                                    var checkpoint = new MigrationCheckpoint
                                    {
                                        Mode = _settings.Mode,
                                        LastMemberId = lastProcessedMemberId,
                                        LastBundleId = null,
                                        Timestamp = DateTime.UtcNow,
                                        Status = MigrationStatus.Phase1MigratingMembers
                                    };
                                    _ = _checkpointService.SaveCheckpointAsync(checkpoint); // Fire and forget - errors logged in service
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }
                catch (Exception ex)
                {
                    LogException(ex, "Consumer error while processing member batch");
                    cancellationTokenSource.Cancel();
                    throw;
                }
            }, cancellationTokenSource.Token);
            
            consumerTasks.Add(consumerTask);
        }

        // Wait for producer(s) and all consumers to complete
        try
        {
            await Task.WhenAll(producerTasks);
            await Task.WhenAll(consumerTasks);
            
            // Save final checkpoint after Phase 1 completes
            if (_checkpointService != null && lastProcessedMemberId.HasValue)
            {
                var checkpoint = new MigrationCheckpoint
                {
                    Mode = _settings.Mode,
                    LastMemberId = lastProcessedMemberId,
                    LastBundleId = null,
                    Timestamp = DateTime.UtcNow,
                    Status = MigrationStatus.Phase1Completed
                };
                await _checkpointService.SaveCheckpointAsync(checkpoint);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during concurrent migration: {ex.Message}");
            cancellationTokenSource.Cancel();
            throw;
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }

        return processedCount;
    }

    private async Task<int> MigrateBundlesAndUpdateMembersConcurrentlyAsync(
        IMongoCollection<Models.MongoDB.MemberDocumentEmbedding> collection,
        long totalCount,
        long? startFromId = null)
    {
        var processedCount = 0;
        var startTime = DateTime.UtcNow;
        var cancellationTokenSource = new CancellationTokenSource();
        
        // Create a bounded channel for producer-consumer pattern
        var channel = Channel.CreateBounded<BundleBatch>(new BoundedChannelOptions(_settings.MaxChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        // Create partitions if parallel producers are enabled
        var bundlePartitions = await CreateBundlePartitionsAsync();
        var usePartitioning = bundlePartitions.Count > 0;
        
        if (usePartitioning)
        {
            Console.WriteLine($"Using {_settings.ParallelBundleProducers} parallel producers for bundles");
        }

        // Producer tasks: Read batches from PostgreSQL (one or multiple)
        var producerTasks = new List<Task>();
        var batchNumberCounter = 0;
        var batchNumberLock = new object();
        
        if (usePartitioning && startFromId == null)
        {
            // Parallel producers with partitioning (only when not resuming from checkpoint)
            foreach (var partition in bundlePartitions)
            {
                var producerTask = Task.Run(async () =>
                {
                    try
                    {
                        long? lastBundleId = null;
                        
                        while (!cancellationTokenSource.Token.IsCancellationRequested)
                        {
                            var bundlesBatch = await _postgreSqlRepository.GetBundlesBatchByIdRangeAsync(
                                partition.StartId, partition.EndId, lastBundleId, _settings.BatchSize);
                            
                            if (!bundlesBatch.Any())
                            {
                                break;
                            }
                            
                            int currentBatchNumber;
                            lock (batchNumberLock)
                            {
                                currentBatchNumber = ++batchNumberCounter;
                            }
                            
                            await channel.Writer.WriteAsync(new BundleBatch
                            {
                                Bundles = bundlesBatch,
                                LastBundleId = bundlesBatch.Last().Id,
                                BatchNumber = currentBatchNumber,
                                ProducerId = partition.PartitionId
                            });
                            
                            lastBundleId = bundlesBatch.Last().Id;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogException(ex, $"Producer {partition.PartitionId} error while reading bundles batch (embedding mode)");
                        throw;
                    }
                }, cancellationTokenSource.Token);
                
                producerTasks.Add(producerTask);
            }
            
            // Wait for all producers and complete the channel
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(producerTasks);
                    channel.Writer.Complete();
                }
                catch (Exception ex)
                {
                    channel.Writer.Complete(ex);
                    throw;
                }
            });
        }
        else
        {
            // Single producer (original behavior or when resuming from checkpoint)
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    long? lastBundleId = startFromId;
                    var currentBatchNumber = 0;
                    
                    while (!cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        var bundlesBatch = await _postgreSqlRepository.GetBundlesBatchAsync(lastBundleId, _settings.BatchSize);
                        
                        if (!bundlesBatch.Any())
                        {
                            break;
                        }
                        
                        await channel.Writer.WriteAsync(new BundleBatch
                        {
                            Bundles = bundlesBatch,
                            LastBundleId = bundlesBatch.Last().Id,
                            BatchNumber = ++currentBatchNumber,
                            ProducerId = 0
                        });
                        
                        lastBundleId = bundlesBatch.Last().Id;
                    }
                    
                    channel.Writer.Complete();
                }
                catch (Exception ex)
                {
                    LogException(ex, "Producer error while reading bundles batch (embedding mode)");
                    channel.Writer.Complete(ex);
                    throw;
                }
            }, cancellationTokenSource.Token);
            
            producerTasks.Add(producerTask);
        }

        // Consumer tasks: Process and update member documents
        var consumerTasks = new List<Task>();
        var lockObject = new object();
        long? lastProcessedBundleId = startFromId;
        
        for (int i = 0; i < _settings.ConcurrentBatchProcessors; i++)
        {
            var consumerTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var batch in channel.Reader.ReadAllAsync(cancellationTokenSource.Token))
                    {
                        var batchStartTime = DateTime.UtcNow;
                        
                        // Convert bundles in parallel
                        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _settings.MaxDegreeOfParallelism };
                        var bundlesByMember = new System.Collections.Concurrent.ConcurrentDictionary<Guid, System.Collections.Concurrent.ConcurrentBag<Models.MongoDB.BundleEmbedded>>();
                        
                        Parallel.ForEach(batch.Bundles, parallelOptions, bundle =>
                        {
                            var bundleEmbedded = DataConverter.ConvertToBundleEmbedded(bundle);
                            var memberBundles = bundlesByMember.GetOrAdd(bundle.MemberId, _ => new System.Collections.Concurrent.ConcurrentBag<Models.MongoDB.BundleEmbedded>());
                            memberBundles.Add(bundleEmbedded);
                        });
                        
                        // Perform bulk updates
                        var bundleUpdateDict = bundlesByMember.ToDictionary(
                            kvp => kvp.Key,
                            kvp => kvp.Value.ToList()
                        );
                        
                        await UpdateMemberBundlesAsync(collection, bundleUpdateDict, cancellationTokenSource.Token);
                        
                        var batchTime = (DateTime.UtcNow - batchStartTime).TotalSeconds;
                        
                        lock (lockObject)
                        {
                            processedCount += batch.Bundles.Count;
                            lastProcessedBundleId = batch.LastBundleId;
                            
                            var elapsedTime = (DateTime.UtcNow - startTime).TotalSeconds;
                            var avgTimePerRecord = processedCount > 0 ? elapsedTime / processedCount : 0;
                            var estimatedRemainingTime = avgTimePerRecord * (totalCount - processedCount);
                            
                            Console.WriteLine($"[Bundle Batch {batch.BatchNumber}] Processed {batch.Bundles.Count} bundles in {batchTime:F2}s");
                            Console.WriteLine($"Progress: {processedCount}/{totalCount} bundles ({(processedCount * 100.0 / totalCount):F2}%) - Est. remaining: {TimeSpan.FromSeconds(estimatedRemainingTime):hh\\:mm\\:ss}");
                            
                            // Save checkpoint periodically
                            if (_checkpointService != null && batch.BatchNumber % _settings.CheckpointInterval == 0)
                            {
                                var checkpoint = new MigrationCheckpoint
                                {
                                    Mode = _settings.Mode,
                                    LastMemberId = null, // Phase 1 already completed
                                    LastBundleId = lastProcessedBundleId,
                                    Timestamp = DateTime.UtcNow,
                                    Status = MigrationStatus.Phase2MigratingBundles
                                };
                                _ = _checkpointService.SaveCheckpointAsync(checkpoint); // Fire and forget - errors logged in service
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }
                catch (Exception ex)
                {
                    LogException(ex, "Consumer error while processing bundle batch");
                    cancellationTokenSource.Cancel();
                    throw;
                }
            }, cancellationTokenSource.Token);
            
            consumerTasks.Add(consumerTask);
        }

        // Wait for producer(s) and all consumers to complete
        try
        {
            await Task.WhenAll(producerTasks);
            await Task.WhenAll(consumerTasks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during concurrent migration: {ex.Message}");
            cancellationTokenSource.Cancel();
            throw;
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }

        return processedCount;
    }

    private async Task UpdateMemberBundlesAsync(
        IMongoCollection<Models.MongoDB.MemberDocumentEmbedding> collection, 
        Dictionary<Guid, List<Models.MongoDB.BundleEmbedded>> bundlesByMemberId,
        CancellationToken cancellationToken = default)
    {
        var bulkOps = new List<WriteModel<Models.MongoDB.MemberDocumentEmbedding>>();
        
        foreach (var kvp in bundlesByMemberId)
        {
            var filter = Builders<Models.MongoDB.MemberDocumentEmbedding>.Filter.Eq(m => m.Id, kvp.Key);
            var update = Builders<Models.MongoDB.MemberDocumentEmbedding>.Update.PushEach(m => m.Bundles, kvp.Value);
            bulkOps.Add(new UpdateOneModel<Models.MongoDB.MemberDocumentEmbedding>(filter, update));
        }
        
        if (bulkOps.Any())
        {
            await collection.BulkWriteAsync(bulkOps, new BulkWriteOptions { IsOrdered = false }, cancellationToken);
        }
    }

    private async Task MigrateReferencingModeAsync(MigrationCheckpoint? checkpoint)
    {
        Console.WriteLine("Counting records in PostgreSQL...");
        
        var totalMembers = await _postgreSqlRepository.GetMembersCountAsync();
        var totalBundles = await _postgreSqlRepository.GetBundlesCountAsync();
        
        Console.WriteLine($"Found {totalMembers} members and {totalBundles} bundles to migrate");

        var membersCollection = _mongoDbRepository.GetMembersCollection();
        var bundlesCollection = _mongoDbRepository.GetBundlesCollection();
        
        // Check if collections already have data
        var existingMembersCount = await membersCollection.CountDocumentsAsync(FilterDefinition<Models.MongoDB.MemberDocument>.Empty);
        var existingBundlesCount = await bundlesCollection.CountDocumentsAsync(FilterDefinition<Models.MongoDB.BundleDocument>.Empty);
        
        // Only drop if not resuming from checkpoint
        if ((existingMembersCount > 0 || existingBundlesCount > 0) && checkpoint == null)
        {
            Console.WriteLine($"WARNING: MongoDB collections already contain data (Members: {existingMembersCount}, Bundles: {existingBundlesCount})!");
            Console.WriteLine("Dropping existing collections to avoid duplicate key errors...");
            
            if (existingMembersCount > 0)
            {
                await _mongoDbRepository.DropMembersCollectionAsync();
                Console.WriteLine("Members collection dropped successfully.");
            }
            
            if (existingBundlesCount > 0)
            {
                await _mongoDbRepository.DropBundlesCollectionAsync();
                Console.WriteLine("Bundles collection dropped successfully.");
            }
            
            // Re-get the collection references after dropping
            membersCollection = _mongoDbRepository.GetMembersCollection();
            bundlesCollection = _mongoDbRepository.GetBundlesCollection();
        }
        else if ((existingMembersCount > 0 || existingBundlesCount > 0) && checkpoint != null)
        {
            Console.WriteLine($"Resuming migration. MongoDB collections contain (Members: {existingMembersCount}, Bundles: {existingBundlesCount}).");
        }
        
        Console.WriteLine("Skipping index creation (will create after migration for better performance)...");

        var startTime = DateTime.UtcNow;
        var lastMemberId = checkpoint?.LastMemberId;
        var lastBundleId = checkpoint?.LastBundleId;

        // Migrate members with concurrent processing
        Console.WriteLine($"Starting concurrent members migration with {_settings.ConcurrentBatchProcessors} processors...");
        if (lastMemberId.HasValue)
        {
            Console.WriteLine($"Resuming from Member ID: {lastMemberId}");
        }
        
        var processedMemberCount = await MigrateMembersConcurrentlyAsync(membersCollection, totalMembers, lastMemberId);
        
        var membersMigrationTime = (DateTime.UtcNow - startTime).TotalSeconds;
        Console.WriteLine($"Members migration completed in {TimeSpan.FromSeconds(membersMigrationTime):hh\\:mm\\:ss}: {processedMemberCount} members migrated");

        // Migrate bundles with concurrent processing
        Console.WriteLine($"Starting concurrent bundles migration with {_settings.ConcurrentBatchProcessors} processors...");
        if (lastBundleId.HasValue)
        {
            Console.WriteLine($"Resuming from Bundle ID: {lastBundleId}");
        }
        
        var bundlesStartTime = DateTime.UtcNow;
        var processedBundleCount = await MigrateBundlesConcurrentlyAsync(bundlesCollection, totalBundles, lastBundleId);
        
        var bundlesMigrationTime = (DateTime.UtcNow - bundlesStartTime).TotalSeconds;
        Console.WriteLine($"Bundles migration completed in {TimeSpan.FromSeconds(bundlesMigrationTime):hh\\:mm\\:ss}: {processedBundleCount} bundles migrated");
        
        Console.WriteLine("Creating indexes...");
        var indexStartTime = DateTime.UtcNow;
        await _mongoDbRepository.CreateIndexesForReferencingAsync();
        var indexTime = (DateTime.UtcNow - indexStartTime).TotalSeconds;
        Console.WriteLine($"Indexes created in {TimeSpan.FromSeconds(indexTime):hh\\:mm\\:ss}");
        
        var totalTime = (DateTime.UtcNow - startTime).TotalSeconds;
        Console.WriteLine($"Total migration time: {TimeSpan.FromSeconds(totalTime):hh\\:mm\\:ss}");
    }

    private async Task<int> MigrateMembersConcurrentlyAsync(
        IMongoCollection<Models.MongoDB.MemberDocument> collection,
        long totalCount,
        Guid? startFromId = null)
    {
        var processedCount = 0;
        var startTime = DateTime.UtcNow;
        var cancellationTokenSource = new CancellationTokenSource();
        
        // Create a bounded channel for producer-consumer pattern
        var channel = Channel.CreateBounded<MemberBatch>(new BoundedChannelOptions(_settings.MaxChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        // Create partitions if parallel producers are enabled
        var memberPartitions = await CreateMemberPartitionsAsync();
        var usePartitioning = memberPartitions.Count > 0;
        
        if (usePartitioning)
        {
            Console.WriteLine($"Using {_settings.ParallelMemberProducers} parallel producers for members");
        }

        // Producer tasks: Read batches from PostgreSQL (one or multiple)
        var producerTasks = new List<Task>();
        var batchNumberCounter = 0;
        var batchNumberLock = new object();
        
        if (usePartitioning && startFromId == null)
        {
            // Parallel producers with partitioning (only when not resuming from checkpoint)
            foreach (var partition in memberPartitions)
            {
                var producerTask = Task.Run(async () =>
                {
                    try
                    {
                        Guid? lastMemberId = null;
                        
                        while (!cancellationTokenSource.Token.IsCancellationRequested)
                        {
                            var membersBatch = await _postgreSqlRepository.GetMembersBatchByUpdateAtRangeAsync(
                                partition.StartUpdateAt, partition.EndUpdateAt, lastMemberId, _settings.BatchSize);
                            
                            if (!membersBatch.Any())
                            {
                                break;
                            }
                            
                            int currentBatchNumber;
                            lock (batchNumberLock)
                            {
                                currentBatchNumber = ++batchNumberCounter;
                            }
                            
                            await channel.Writer.WriteAsync(new MemberBatch
                            {
                                Members = membersBatch,
                                LastMemberId = membersBatch.Last().Id,
                                BatchNumber = currentBatchNumber,
                                ProducerId = partition.PartitionId
                            });
                            
                            lastMemberId = membersBatch.Last().Id;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogException(ex, $"Producer {partition.PartitionId} error while reading members batch (referencing mode)");
                        throw;
                    }
                }, cancellationTokenSource.Token);
                
                producerTasks.Add(producerTask);
            }
            
            // Wait for all producers and complete the channel
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(producerTasks);
                    channel.Writer.Complete();
                }
                catch (Exception ex)
                {
                    channel.Writer.Complete(ex);
                    throw;
                }
            });
        }
        else
        {
            // Single producer (original behavior or when resuming from checkpoint)
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    Guid? lastMemberId = startFromId;
                    var currentBatchNumber = 0;
                    
                    while (!cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        var membersBatch = await _postgreSqlRepository.GetMembersBatchAsync(lastMemberId, _settings.BatchSize);
                        
                        if (!membersBatch.Any())
                        {
                            break;
                        }
                        
                        await channel.Writer.WriteAsync(new MemberBatch
                        {
                            Members = membersBatch,
                            LastMemberId = membersBatch.Last().Id,
                            BatchNumber = ++currentBatchNumber,
                            ProducerId = 0
                        });
                        
                        lastMemberId = membersBatch.Last().Id;
                    }
                    
                    channel.Writer.Complete();
                }
                catch (Exception ex)
                {
                    LogException(ex, "Producer error while reading members batch (referencing mode)");
                    channel.Writer.Complete(ex);
                    throw;
                }
            }, cancellationTokenSource.Token);
            
            producerTasks.Add(producerTask);
        }

        // Consumer tasks: Process and write batches to MongoDB
        var consumerTasks = new List<Task>();
        var lockObject = new object();
        Guid? lastProcessedMemberId = startFromId;
        
        for (int i = 0; i < _settings.ConcurrentBatchProcessors; i++)
        {
            var consumerTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var batch in channel.Reader.ReadAllAsync(cancellationTokenSource.Token))
                    {
                        var batchStartTime = DateTime.UtcNow;
                        
                        // Convert to MongoDB documents in parallel for better CPU utilization
                        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _settings.MaxDegreeOfParallelism };
                        var documents = new System.Collections.Concurrent.ConcurrentBag<Models.MongoDB.MemberDocument>();
                        
                        Parallel.ForEach(batch.Members, parallelOptions, member =>
                        {
                            var document = DataConverter.ConvertToMemberDocument(member);
                            documents.Add(document);
                        });
                        
                        var documentsList = documents.ToList();

                        if (documentsList.Any())
                        {
                            // Use ReplaceOne with upsert to handle potential duplicates when resuming from checkpoint
                            var bulkOps = documentsList.Select(doc => 
                                new ReplaceOneModel<Models.MongoDB.MemberDocument>(
                                    Builders<Models.MongoDB.MemberDocument>.Filter.Eq(m => m.Id, doc.Id),
                                    doc)
                                {
                                    IsUpsert = true
                                });
                            
                            await collection.BulkWriteAsync(bulkOps, new BulkWriteOptions { IsOrdered = false }, cancellationTokenSource.Token);
                            
                            var batchTime = (DateTime.UtcNow - batchStartTime).TotalSeconds;
                            
                            lock (lockObject)
                            {
                                processedCount += documentsList.Count;
                                lastProcessedMemberId = batch.LastMemberId;
                                
                                var elapsedTime = (DateTime.UtcNow - startTime).TotalSeconds;
                                var avgTimePerRecord = processedCount > 0 ? elapsedTime / processedCount : 0;
                                var estimatedRemainingTime = avgTimePerRecord * (totalCount - processedCount);
                                
                                Console.WriteLine($"[Member Batch {batch.BatchNumber}] Processed {batch.Members.Count} members in {batchTime:F2}s");
                                Console.WriteLine($"Progress: {processedCount}/{totalCount} members ({(processedCount * 100.0 / totalCount):F2}%) - Est. remaining: {TimeSpan.FromSeconds(estimatedRemainingTime):hh\\:mm\\:ss}");
                                
                                // Save checkpoint periodically
                                if (_checkpointService != null && batch.BatchNumber % _settings.CheckpointInterval == 0)
                                {
                                    var checkpoint = new MigrationCheckpoint
                                    {
                                        Mode = _settings.Mode,
                                        LastMemberId = lastProcessedMemberId,
                                        LastBundleId = null,
                                        Timestamp = DateTime.UtcNow,
                                        Status = MigrationStatus.MigratingMembers
                                    };
                                    _ = _checkpointService.SaveCheckpointAsync(checkpoint); // Fire and forget - errors logged in service
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }
                catch (Exception ex)
                {
                    LogException(ex, "Consumer error while processing member documents (referencing mode)");
                    cancellationTokenSource.Cancel();
                    throw;
                }
            }, cancellationTokenSource.Token);
            
            consumerTasks.Add(consumerTask);
        }

        // Wait for producer(s) and all consumers to complete (referencing mode members)
        try
        {
            await Task.WhenAll(producerTasks);
            await Task.WhenAll(consumerTasks);
            
            // Save final checkpoint after members migration completes
            if (_checkpointService != null && lastProcessedMemberId.HasValue)
            {
                var checkpoint = new MigrationCheckpoint
                {
                    Mode = _settings.Mode,
                    LastMemberId = lastProcessedMemberId,
                    LastBundleId = null,
                    Timestamp = DateTime.UtcNow,
                    Status = MigrationStatus.MembersCompleted
                };
                await _checkpointService.SaveCheckpointAsync(checkpoint);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during concurrent migration: {ex.Message}");
            cancellationTokenSource.Cancel();
            throw;
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }

        return processedCount;
    }

    private async Task<int> MigrateBundlesConcurrentlyAsync(
        IMongoCollection<Models.MongoDB.BundleDocument> collection,
        long totalCount,
        long? startFromId = null)
    {
        var processedCount = 0;
        var startTime = DateTime.UtcNow;
        var cancellationTokenSource = new CancellationTokenSource();
        
        // Create a bounded channel for producer-consumer pattern
        var channel = Channel.CreateBounded<BundleBatch>(new BoundedChannelOptions(_settings.MaxChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        // Create partitions if parallel producers are enabled
        var bundlePartitions = await CreateBundlePartitionsAsync();
        var usePartitioning = bundlePartitions.Count > 0;
        
        if (usePartitioning)
        {
            Console.WriteLine($"Using {_settings.ParallelBundleProducers} parallel producers for bundles");
        }

        // Producer tasks: Read batches from PostgreSQL (one or multiple)
        var producerTasks = new List<Task>();
        var batchNumberCounter = 0;
        var batchNumberLock = new object();
        
        if (usePartitioning && startFromId == null)
        {
            // Parallel producers with partitioning (only when not resuming from checkpoint)
            foreach (var partition in bundlePartitions)
            {
                var producerTask = Task.Run(async () =>
                {
                    try
                    {
                        long? lastBundleId = null;
                        
                        while (!cancellationTokenSource.Token.IsCancellationRequested)
                        {
                            var bundlesBatch = await _postgreSqlRepository.GetBundlesBatchByIdRangeAsync(
                                partition.StartId, partition.EndId, lastBundleId, _settings.BatchSize);
                            
                            if (!bundlesBatch.Any())
                            {
                                break;
                            }
                            
                            int currentBatchNumber;
                            lock (batchNumberLock)
                            {
                                currentBatchNumber = ++batchNumberCounter;
                            }
                            
                            await channel.Writer.WriteAsync(new BundleBatch
                            {
                                Bundles = bundlesBatch,
                                LastBundleId = bundlesBatch.Last().Id,
                                BatchNumber = currentBatchNumber,
                                ProducerId = partition.PartitionId
                            });
                            
                            lastBundleId = bundlesBatch.Last().Id;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogException(ex, $"Producer {partition.PartitionId} error while reading bundles batch (referencing mode)");
                        throw;
                    }
                }, cancellationTokenSource.Token);
                
                producerTasks.Add(producerTask);
            }
            
            // Wait for all producers and complete the channel
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(producerTasks);
                    channel.Writer.Complete();
                }
                catch (Exception ex)
                {
                    channel.Writer.Complete(ex);
                    throw;
                }
            });
        }
        else
        {
            // Single producer (original behavior or when resuming from checkpoint)
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    long? lastBundleId = startFromId;
                    var currentBatchNumber = 0;
                    
                    while (!cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        var bundlesBatch = await _postgreSqlRepository.GetBundlesBatchAsync(lastBundleId, _settings.BatchSize);
                        
                        if (!bundlesBatch.Any())
                        {
                            break;
                        }
                        
                        await channel.Writer.WriteAsync(new BundleBatch
                        {
                            Bundles = bundlesBatch,
                            LastBundleId = bundlesBatch.Last().Id,
                            BatchNumber = ++currentBatchNumber,
                            ProducerId = 0
                        });
                        
                        lastBundleId = bundlesBatch.Last().Id;
                    }
                    
                    channel.Writer.Complete();
                }
                catch (Exception ex)
                {
                    LogException(ex, "Producer error while reading bundles batch (referencing mode)");
                    channel.Writer.Complete(ex);
                    throw;
                }
            }, cancellationTokenSource.Token);
            
            producerTasks.Add(producerTask);
        }

        // Consumer tasks: Process and write batches to MongoDB
        var consumerTasks = new List<Task>();
        var lockObject = new object();
        long? lastProcessedBundleId = startFromId;
        
        for (int i = 0; i < _settings.ConcurrentBatchProcessors; i++)
        {
            var consumerTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var batch in channel.Reader.ReadAllAsync(cancellationTokenSource.Token))
                    {
                        var batchStartTime = DateTime.UtcNow;
                        
                        // Convert to MongoDB documents in parallel for better CPU utilization
                        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _settings.MaxDegreeOfParallelism };
                        var documents = new System.Collections.Concurrent.ConcurrentBag<Models.MongoDB.BundleDocument>();
                        
                        Parallel.ForEach(batch.Bundles, parallelOptions, bundle =>
                        {
                            var document = DataConverter.ConvertToBundleDocument(bundle);
                            documents.Add(document);
                        });
                        
                        var documentsList = documents.ToList();

                        if (documentsList.Any())
                        {
                            // Use ReplaceOne with upsert to handle potential duplicates when resuming from checkpoint
                            var bulkOps = documentsList.Select(doc => 
                                new ReplaceOneModel<Models.MongoDB.BundleDocument>(
                                    Builders<Models.MongoDB.BundleDocument>.Filter.Eq(b => b.Id, doc.Id),
                                    doc)
                                {
                                    IsUpsert = true
                                });
                            
                            await collection.BulkWriteAsync(bulkOps, new BulkWriteOptions { IsOrdered = false }, cancellationTokenSource.Token);
                            
                            var batchTime = (DateTime.UtcNow - batchStartTime).TotalSeconds;
                            
                            lock (lockObject)
                            {
                                processedCount += documentsList.Count;
                                lastProcessedBundleId = batch.LastBundleId;
                                
                                var elapsedTime = (DateTime.UtcNow - startTime).TotalSeconds;
                                var avgTimePerRecord = processedCount > 0 ? elapsedTime / processedCount : 0;
                                var estimatedRemainingTime = avgTimePerRecord * (totalCount - processedCount);
                                
                                Console.WriteLine($"[Bundle Batch {batch.BatchNumber}] Processed {batch.Bundles.Count} bundles in {batchTime:F2}s");
                                Console.WriteLine($"Progress: {processedCount}/{totalCount} bundles ({(processedCount * 100.0 / totalCount):F2}%) - Est. remaining: {TimeSpan.FromSeconds(estimatedRemainingTime):hh\\:mm\\:ss}");
                                
                                // Save checkpoint periodically
                                if (_checkpointService != null && batch.BatchNumber % _settings.CheckpointInterval == 0)
                                {
                                    var checkpoint = new MigrationCheckpoint
                                    {
                                        Mode = _settings.Mode,
                                        LastMemberId = null, // Members already completed
                                        LastBundleId = lastProcessedBundleId,
                                        Timestamp = DateTime.UtcNow,
                                        Status = MigrationStatus.MigratingBundles
                                    };
                                    _ = _checkpointService.SaveCheckpointAsync(checkpoint); // Fire and forget - errors logged in service
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }
                catch (Exception ex)
                {
                    LogException(ex, "Consumer error while processing bundle documents (referencing mode)");
                    cancellationTokenSource.Cancel();
                    throw;
                }
            }, cancellationTokenSource.Token);
            
            consumerTasks.Add(consumerTask);
        }

        // Wait for producer(s) and all consumers to complete (referencing mode bundles)
        try
        {
            await Task.WhenAll(producerTasks);
            await Task.WhenAll(consumerTasks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during concurrent migration: {ex.Message}");
            cancellationTokenSource.Cancel();
            throw;
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }

        return processedCount;
    }
}
