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
}

internal class BundleBatch
{
    public List<Models.Bundle> Bundles { get; set; } = new();
    public long? LastBundleId { get; set; }
    public int BatchNumber { get; set; }
}

// Helper class for ID range partitioning
internal class MemberIdRange
{
    public Guid? StartId { get; set; }
    public Guid? EndId { get; set; }
    public int PartitionIndex { get; set; }
}

internal class BundleIdRange
{
    public long? StartId { get; set; }
    public long? EndId { get; set; }
    public int PartitionIndex { get; set; }
}

public class MigrationService
{
    private readonly PostgreSqlRepository _postgreSqlRepository;
    private readonly MongoDbRepository _mongoDbRepository;
    private readonly MigrationSettings _settings;

    public MigrationService(
        PostgreSqlRepository postgreSqlRepository,
        MongoDbRepository mongoDbRepository,
        MigrationSettings settings)
    {
        _postgreSqlRepository = postgreSqlRepository;
        _mongoDbRepository = mongoDbRepository;
        _settings = settings;
    }

    public async Task MigrateAsync()
    {
        Console.WriteLine($"Starting migration in {_settings.Mode} mode...");
        Console.WriteLine($"Batch size: {_settings.BatchSize}");
        Console.WriteLine($"Concurrent producers: {_settings.ConcurrentProducers}");
        Console.WriteLine($"Concurrent batch processors: {_settings.ConcurrentBatchProcessors}");

        try
        {
            if (_settings.Mode == MigrationMode.Embedding)
            {
                await MigrateEmbeddingModeAsync();
            }
            else
            {
                await MigrateReferencingModeAsync();
            }

            Console.WriteLine("Migration completed successfully!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Migration failed: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    private async Task MigrateEmbeddingModeAsync()
    {
        Console.WriteLine("Counting records in PostgreSQL...");
        
        var totalMembers = await _postgreSqlRepository.GetMembersCountAsync();
        var totalBundles = await _postgreSqlRepository.GetBundlesCountAsync();
        Console.WriteLine($"Found {totalMembers} members and {totalBundles} bundles to migrate");

        var membersCollection = _mongoDbRepository.GetMembersEmbeddingCollection();
        
        // Check if collection already has data
        var existingCount = await membersCollection.CountDocumentsAsync(FilterDefinition<Models.MongoDB.MemberDocumentEmbedding>.Empty);
        if (existingCount > 0)
        {
            Console.WriteLine($"WARNING: MongoDB collection already contains {existingCount} documents!");
            Console.WriteLine("Dropping existing collection to avoid duplicate key errors...");
            await _mongoDbRepository.DropMembersEmbeddingCollectionAsync();
            Console.WriteLine("Collection dropped successfully.");
            // Re-get the collection reference after dropping
            membersCollection = _mongoDbRepository.GetMembersEmbeddingCollection();
        }
        
        Console.WriteLine("Skipping index creation (will create after migration for better performance)...");

        var startTime = DateTime.UtcNow;

        // Phase 1: Migrate all members first (without bundles for better performance)
        Console.WriteLine($"Phase 1: Migrating members without bundles using {_settings.ConcurrentProducers} concurrent producers and {_settings.ConcurrentBatchProcessors} concurrent processors...");
        
        var processedMemberCount = await MigrateMembersWithoutBundlesConcurrentlyAsync(membersCollection, totalMembers);
        
        var membersTime = (DateTime.UtcNow - startTime).TotalSeconds;
        Console.WriteLine($"Phase 1 completed in {TimeSpan.FromSeconds(membersTime):hh\\:mm\\:ss}: {processedMemberCount} members migrated");

        // Phase 2: Migrate bundles and update member documents
        Console.WriteLine($"Phase 2: Migrating bundles and updating member documents using {_settings.ConcurrentProducers} concurrent producers and {_settings.ConcurrentBatchProcessors} concurrent processors...");
        
        var bundlesStartTime = DateTime.UtcNow;
        var processedBundleCount = await MigrateBundlesAndUpdateMembersConcurrentlyAsync(membersCollection, totalBundles);
        
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
        long totalCount)
    {
        var processedCount = 0;
        var startTime = DateTime.UtcNow;
        var cancellationTokenSource = new CancellationTokenSource();
        
        // Create a bounded channel for producer-consumer pattern
        var channel = Channel.CreateBounded<MemberBatch>(new BoundedChannelOptions(_settings.MaxChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        // Calculate ID ranges for multiple producers
        var idRanges = await CalculateMemberIdRangesAsync(_settings.ConcurrentProducers);
        var batchNumberCounter = 0;
        var batchNumberLock = new object();

        // Producer tasks: Multiple producers reading different ID ranges from PostgreSQL
        var producerTasks = new List<Task>();
        
        for (int p = 0; p < idRanges.Count; p++)
        {
            var range = idRanges[p];
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    Guid? lastMemberId = range.StartId;
                    
                    while (!cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        List<Models.Member> membersBatch;
                        
                        if (range.EndId.HasValue)
                        {
                            // Query with range constraint
                            membersBatch = await _postgreSqlRepository.GetMembersBatchInRangeAsync(lastMemberId, range.EndId, _settings.BatchSize);
                        }
                        else
                        {
                            // Query without upper limit (last partition)
                            membersBatch = await _postgreSqlRepository.GetMembersBatchAsync(lastMemberId, _settings.BatchSize);
                        }
                        
                        if (!membersBatch.Any())
                        {
                            break;
                        }
                        
                        int currentBatchNumber;
                        lock (batchNumberLock)
                        {
                            currentBatchNumber = ++batchNumberCounter;
                        }
                        
                        // Don't pass cancellation token to WriteAsync to ensure proper channel completion
                        await channel.Writer.WriteAsync(new MemberBatch
                        {
                            Members = membersBatch,
                            LastMemberId = membersBatch.Last().Id,
                            BatchNumber = currentBatchNumber
                        });
                        
                        lastMemberId = membersBatch.Last().Id;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Producer {range.PartitionIndex} error: {ex.Message}");
                    throw;
                }
            }, cancellationTokenSource.Token);
            
            producerTasks.Add(producerTask);
        }

        // Monitor task to complete channel when all producers are done
        var monitorTask = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(producerTasks);
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Producer monitor error: {ex.Message}");
                channel.Writer.Complete(ex);
                throw;
            }
        });

        // Consumer tasks: Process and write batches to MongoDB
        var consumerTasks = new List<Task>();
        var lockObject = new object();
        
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
                            var options = new InsertManyOptions { IsOrdered = false };
                            await collection.InsertManyAsync(documentsList, options, cancellationTokenSource.Token);
                            
                            var batchTime = (DateTime.UtcNow - batchStartTime).TotalSeconds;
                            
                            lock (lockObject)
                            {
                                processedCount += documentsList.Count;
                                
                                var elapsedTime = (DateTime.UtcNow - startTime).TotalSeconds;
                                var avgTimePerRecord = processedCount > 0 ? elapsedTime / processedCount : 0;
                                var estimatedRemainingTime = avgTimePerRecord * (totalCount - processedCount);
                                
                                Console.WriteLine($"[Member Batch {batch.BatchNumber}] Processed {batch.Members.Count} members in {batchTime:F2}s");
                                Console.WriteLine($"Progress: {processedCount}/{totalCount} members ({(processedCount * 100.0 / totalCount):F2}%) - Est. remaining: {TimeSpan.FromSeconds(estimatedRemainingTime):hh\\:mm\\:ss}");
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
                    Console.WriteLine($"Consumer error: {ex.Message}");
                    cancellationTokenSource.Cancel();
                    throw;
                }
            }, cancellationTokenSource.Token);
            
            consumerTasks.Add(consumerTask);
        }

        // Wait for all producers, monitor and all consumers to complete
        try
        {
            await Task.WhenAll(consumerTasks);
            await monitorTask;
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
        long totalCount)
    {
        var processedCount = 0;
        var startTime = DateTime.UtcNow;
        var cancellationTokenSource = new CancellationTokenSource();
        
        // Create a bounded channel for producer-consumer pattern
        var channel = Channel.CreateBounded<BundleBatch>(new BoundedChannelOptions(_settings.MaxChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        // Calculate ID ranges for multiple producers
        var idRanges = await CalculateBundleIdRangesAsync(_settings.ConcurrentProducers);
        var batchNumberCounter = 0;
        var batchNumberLock = new object();

        // Producer tasks: Multiple producers reading different ID ranges from PostgreSQL
        var producerTasks = new List<Task>();
        
        for (int p = 0; p < idRanges.Count; p++)
        {
            var range = idRanges[p];
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    long? lastBundleId = range.StartId;
                    
                    while (!cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        List<Models.Bundle> bundlesBatch;
                        
                        if (range.EndId.HasValue)
                        {
                            // Query with range constraint
                            bundlesBatch = await _postgreSqlRepository.GetBundlesBatchInRangeAsync(lastBundleId, range.EndId, _settings.BatchSize);
                        }
                        else
                        {
                            // Query without upper limit (last partition)
                            bundlesBatch = await _postgreSqlRepository.GetBundlesBatchAsync(lastBundleId, _settings.BatchSize);
                        }
                        
                        if (!bundlesBatch.Any())
                        {
                            break;
                        }
                        
                        int currentBatchNumber;
                        lock (batchNumberLock)
                        {
                            currentBatchNumber = ++batchNumberCounter;
                        }
                        
                        // Don't pass cancellation token to WriteAsync to ensure proper channel completion
                        await channel.Writer.WriteAsync(new BundleBatch
                        {
                            Bundles = bundlesBatch,
                            LastBundleId = bundlesBatch.Last().Id,
                            BatchNumber = currentBatchNumber
                        });
                        
                        lastBundleId = bundlesBatch.Last().Id;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Producer {range.PartitionIndex} error: {ex.Message}");
                    throw;
                }
            }, cancellationTokenSource.Token);
            
            producerTasks.Add(producerTask);
        }

        // Monitor task to complete channel when all producers are done
        var monitorTask = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(producerTasks);
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Producer monitor error: {ex.Message}");
                channel.Writer.Complete(ex);
                throw;
            }
        });

        // Consumer tasks: Process and update member documents
        var consumerTasks = new List<Task>();
        var lockObject = new object();
        
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
                            
                            var elapsedTime = (DateTime.UtcNow - startTime).TotalSeconds;
                            var avgTimePerRecord = processedCount > 0 ? elapsedTime / processedCount : 0;
                            var estimatedRemainingTime = avgTimePerRecord * (totalCount - processedCount);
                            
                            Console.WriteLine($"[Bundle Batch {batch.BatchNumber}] Processed {batch.Bundles.Count} bundles in {batchTime:F2}s");
                            Console.WriteLine($"Progress: {processedCount}/{totalCount} bundles ({(processedCount * 100.0 / totalCount):F2}%) - Est. remaining: {TimeSpan.FromSeconds(estimatedRemainingTime):hh\\:mm\\:ss}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Consumer error: {ex.Message}");
                    cancellationTokenSource.Cancel();
                    throw;
                }
            }, cancellationTokenSource.Token);
            
            consumerTasks.Add(consumerTask);
        }

        // Wait for all producers, monitor and all consumers to complete
        try
        {
            await Task.WhenAll(consumerTasks);
            await monitorTask;
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

    private async Task MigrateReferencingModeAsync()
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
        
        if (existingMembersCount > 0 || existingBundlesCount > 0)
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
        
        Console.WriteLine("Skipping index creation (will create after migration for better performance)...");

        var startTime = DateTime.UtcNow;

        // Migrate members with concurrent processing
        Console.WriteLine($"Starting concurrent members migration with {_settings.ConcurrentProducers} concurrent producers and {_settings.ConcurrentBatchProcessors} processors...");
        
        var processedMemberCount = await MigrateMembersConcurrentlyAsync(membersCollection, totalMembers);
        
        var membersMigrationTime = (DateTime.UtcNow - startTime).TotalSeconds;
        Console.WriteLine($"Members migration completed in {TimeSpan.FromSeconds(membersMigrationTime):hh\\:mm\\:ss}: {processedMemberCount} members migrated");

        // Migrate bundles with concurrent processing
        Console.WriteLine($"Starting concurrent bundles migration with {_settings.ConcurrentProducers} concurrent producers and {_settings.ConcurrentBatchProcessors} processors...");
        
        var bundlesStartTime = DateTime.UtcNow;
        var processedBundleCount = await MigrateBundlesConcurrentlyAsync(bundlesCollection, totalBundles);
        
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
        long totalCount)
    {
        var processedCount = 0;
        var startTime = DateTime.UtcNow;
        var cancellationTokenSource = new CancellationTokenSource();
        
        // Create a bounded channel for producer-consumer pattern
        var channel = Channel.CreateBounded<MemberBatch>(new BoundedChannelOptions(_settings.MaxChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        // Calculate ID ranges for multiple producers
        var idRanges = await CalculateMemberIdRangesAsync(_settings.ConcurrentProducers);
        var batchNumberCounter = 0;
        var batchNumberLock = new object();

        // Producer tasks: Multiple producers reading different ID ranges from PostgreSQL
        var producerTasks = new List<Task>();
        
        for (int p = 0; p < idRanges.Count; p++)
        {
            var range = idRanges[p];
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    Guid? lastMemberId = range.StartId;
                    
                    while (!cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        List<Models.Member> membersBatch;
                        
                        if (range.EndId.HasValue)
                        {
                            // Query with range constraint
                            membersBatch = await _postgreSqlRepository.GetMembersBatchInRangeAsync(lastMemberId, range.EndId, _settings.BatchSize);
                        }
                        else
                        {
                            // Query without upper limit (last partition)
                            membersBatch = await _postgreSqlRepository.GetMembersBatchAsync(lastMemberId, _settings.BatchSize);
                        }
                        
                        if (!membersBatch.Any())
                        {
                            break;
                        }
                        
                        int currentBatchNumber;
                        lock (batchNumberLock)
                        {
                            currentBatchNumber = ++batchNumberCounter;
                        }
                        
                        // Don't pass cancellation token to WriteAsync to ensure proper channel completion
                        await channel.Writer.WriteAsync(new MemberBatch
                        {
                            Members = membersBatch,
                            LastMemberId = membersBatch.Last().Id,
                            BatchNumber = currentBatchNumber
                        });
                        
                        lastMemberId = membersBatch.Last().Id;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Producer {range.PartitionIndex} error: {ex.Message}");
                    throw;
                }
            }, cancellationTokenSource.Token);
            
            producerTasks.Add(producerTask);
        }

        // Monitor task to complete channel when all producers are done
        var monitorTask = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(producerTasks);
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Producer monitor error: {ex.Message}");
                channel.Writer.Complete(ex);
                throw;
            }
        });

        // Consumer tasks: Process and write batches to MongoDB
        var consumerTasks = new List<Task>();
        var lockObject = new object();
        
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
                            var options = new InsertManyOptions { IsOrdered = false };
                            await collection.InsertManyAsync(documentsList, options, cancellationTokenSource.Token);
                            
                            var batchTime = (DateTime.UtcNow - batchStartTime).TotalSeconds;
                            
                            lock (lockObject)
                            {
                                processedCount += documentsList.Count;
                                
                                var elapsedTime = (DateTime.UtcNow - startTime).TotalSeconds;
                                var avgTimePerRecord = processedCount > 0 ? elapsedTime / processedCount : 0;
                                var estimatedRemainingTime = avgTimePerRecord * (totalCount - processedCount);
                                
                                Console.WriteLine($"[Member Batch {batch.BatchNumber}] Processed {batch.Members.Count} members in {batchTime:F2}s");
                                Console.WriteLine($"Progress: {processedCount}/{totalCount} members ({(processedCount * 100.0 / totalCount):F2}%) - Est. remaining: {TimeSpan.FromSeconds(estimatedRemainingTime):hh\\:mm\\:ss}");
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
                    Console.WriteLine($"Consumer error: {ex.Message}");
                    cancellationTokenSource.Cancel();
                    throw;
                }
            }, cancellationTokenSource.Token);
            
            consumerTasks.Add(consumerTask);
        }

        // Wait for all producers, monitor and all consumers to complete
        try
        {
            await Task.WhenAll(consumerTasks);
            await monitorTask;
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
        long totalCount)
    {
        var processedCount = 0;
        var startTime = DateTime.UtcNow;
        var cancellationTokenSource = new CancellationTokenSource();
        
        // Create a bounded channel for producer-consumer pattern
        var channel = Channel.CreateBounded<BundleBatch>(new BoundedChannelOptions(_settings.MaxChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        // Calculate ID ranges for multiple producers
        var idRanges = await CalculateBundleIdRangesAsync(_settings.ConcurrentProducers);
        var batchNumberCounter = 0;
        var batchNumberLock = new object();

        // Producer tasks: Multiple producers reading different ID ranges from PostgreSQL
        var producerTasks = new List<Task>();
        
        for (int p = 0; p < idRanges.Count; p++)
        {
            var range = idRanges[p];
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    long? lastBundleId = range.StartId;
                    
                    while (!cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        List<Models.Bundle> bundlesBatch;
                        
                        if (range.EndId.HasValue)
                        {
                            // Query with range constraint
                            bundlesBatch = await _postgreSqlRepository.GetBundlesBatchInRangeAsync(lastBundleId, range.EndId, _settings.BatchSize);
                        }
                        else
                        {
                            // Query without upper limit (last partition)
                            bundlesBatch = await _postgreSqlRepository.GetBundlesBatchAsync(lastBundleId, _settings.BatchSize);
                        }
                        
                        if (!bundlesBatch.Any())
                        {
                            break;
                        }
                        
                        int currentBatchNumber;
                        lock (batchNumberLock)
                        {
                            currentBatchNumber = ++batchNumberCounter;
                        }
                        
                        // Don't pass cancellation token to WriteAsync to ensure proper channel completion
                        await channel.Writer.WriteAsync(new BundleBatch
                        {
                            Bundles = bundlesBatch,
                            LastBundleId = bundlesBatch.Last().Id,
                            BatchNumber = currentBatchNumber
                        });
                        
                        lastBundleId = bundlesBatch.Last().Id;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Producer {range.PartitionIndex} error: {ex.Message}");
                    throw;
                }
            }, cancellationTokenSource.Token);
            
            producerTasks.Add(producerTask);
        }

        // Monitor task to complete channel when all producers are done
        var monitorTask = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(producerTasks);
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Producer monitor error: {ex.Message}");
                channel.Writer.Complete(ex);
                throw;
            }
        });

        // Consumer tasks: Process and write batches to MongoDB
        var consumerTasks = new List<Task>();
        var lockObject = new object();
        
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
                            var options = new InsertManyOptions { IsOrdered = false };
                            await collection.InsertManyAsync(documentsList, options, cancellationTokenSource.Token);
                            
                            var batchTime = (DateTime.UtcNow - batchStartTime).TotalSeconds;
                            
                            lock (lockObject)
                            {
                                processedCount += documentsList.Count;
                                
                                var elapsedTime = (DateTime.UtcNow - startTime).TotalSeconds;
                                var avgTimePerRecord = processedCount > 0 ? elapsedTime / processedCount : 0;
                                var estimatedRemainingTime = avgTimePerRecord * (totalCount - processedCount);
                                
                                Console.WriteLine($"[Bundle Batch {batch.BatchNumber}] Processed {batch.Bundles.Count} bundles in {batchTime:F2}s");
                                Console.WriteLine($"Progress: {processedCount}/{totalCount} bundles ({(processedCount * 100.0 / totalCount):F2}%) - Est. remaining: {TimeSpan.FromSeconds(estimatedRemainingTime):hh\\:mm\\:ss}");
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
                    Console.WriteLine($"Consumer error: {ex.Message}");
                    cancellationTokenSource.Cancel();
                    throw;
                }
            }, cancellationTokenSource.Token);
            
            consumerTasks.Add(consumerTask);
        }

        // Wait for all producers, monitor and all consumers to complete
        try
        {
            await Task.WhenAll(consumerTasks);
            await monitorTask;
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

    private async Task<List<BundleIdRange>> CalculateBundleIdRangesAsync(int numProducers)
    {
        var ranges = new List<BundleIdRange>();
        
        if (numProducers <= 0)
        {
            ranges.Add(new BundleIdRange { StartId = null, EndId = null, PartitionIndex = 0 });
            return ranges;
        }

        if (numProducers == 1)
        {
            ranges.Add(new BundleIdRange { StartId = null, EndId = null, PartitionIndex = 0 });
            return ranges;
        }

        var (minId, maxId) = await _postgreSqlRepository.GetBundlesIdRangeAsync();
        
        if (!minId.HasValue || !maxId.HasValue)
        {
            ranges.Add(new BundleIdRange { StartId = null, EndId = null, PartitionIndex = 0 });
            return ranges;
        }

        var totalRange = maxId.Value - minId.Value + 1;
        var rangeSize = totalRange / numProducers;
        
        for (int i = 0; i < numProducers; i++)
        {
            long? start = i == 0 ? null : minId.Value + (i * rangeSize);
            long? end = i == numProducers - 1 ? null : minId.Value + ((i + 1) * rangeSize) - 1;
            
            ranges.Add(new BundleIdRange
            {
                StartId = start,
                EndId = end,
                PartitionIndex = i
            });
        }
        
        return ranges;
    }

    private async Task<List<MemberIdRange>> CalculateMemberIdRangesAsync(int numProducers)
    {
        var ranges = new List<MemberIdRange>();
        
        if (numProducers <= 0)
        {
            ranges.Add(new MemberIdRange { StartId = null, EndId = null, PartitionIndex = 0 });
            return ranges;
        }

        if (numProducers == 1)
        {
            ranges.Add(new MemberIdRange { StartId = null, EndId = null, PartitionIndex = 0 });
            return ranges;
        }

        // For UUIDs, we'll query sample IDs at regular intervals to create partitions
        var totalMembers = await _postgreSqlRepository.GetMembersCountAsync();
        
        if (totalMembers == 0)
        {
            ranges.Add(new MemberIdRange { StartId = null, EndId = null, PartitionIndex = 0 });
            return ranges;
        }

        var membersPerProducer = totalMembers / numProducers;
        var sampleIds = await _postgreSqlRepository.GetMemberIdSamplesAsync(numProducers - 1, membersPerProducer);
        
        for (int i = 0; i < numProducers; i++)
        {
            Guid? startId = i == 0 ? null : sampleIds[i - 1];
            Guid? endId = i < numProducers - 1 ? sampleIds[i] : null;
            
            ranges.Add(new MemberIdRange
            {
                StartId = startId,
                EndId = endId,
                PartitionIndex = i
            });
        }
        
        return ranges;
    }
}
