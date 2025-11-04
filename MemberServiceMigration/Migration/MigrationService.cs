using MemberServiceMigration.Configuration;
using MemberServiceMigration.Database;
using MongoDB.Driver;

namespace MemberServiceMigration.Migration;

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
        Console.WriteLine("Phase 1: Migrating members without bundles...");
        
        var processedMemberCount = 0;
        Guid? lastMemberId = null;
        
        while (true)
        {
            var batchStartTime = DateTime.UtcNow;
            
            var membersBatch = await _postgreSqlRepository.GetMembersBatchAsync(lastMemberId, _settings.BatchSize);
            
            if (!membersBatch.Any())
            {
                break;
            }
            
            // Convert members to documents without bundles in parallel
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _settings.MaxDegreeOfParallelism };
            var documents = new System.Collections.Concurrent.ConcurrentBag<Models.MongoDB.MemberDocumentEmbedding>();
            
            Parallel.ForEach(membersBatch, parallelOptions, member =>
            {
                var document = DataConverter.ConvertToMemberDocumentEmbedding(member, null);
                documents.Add(document);
            });

            var documentsList = documents.ToList();
            if (documentsList.Any())
            {
                var options = new InsertManyOptions { IsOrdered = false };
                await membersCollection.InsertManyAsync(documentsList, options);
                processedMemberCount += documentsList.Count;
                
                var batchTime = (DateTime.UtcNow - batchStartTime).TotalSeconds;
                var elapsedTime = (DateTime.UtcNow - startTime).TotalSeconds;
                var batchNumber = processedMemberCount / _settings.BatchSize;
                var avgTimePerBatch = batchNumber > 0 ? elapsedTime / batchNumber : batchTime;
                var estimatedRemainingBatches = (totalMembers - processedMemberCount) / (double)_settings.BatchSize;
                var estimatedRemainingTime = avgTimePerBatch * estimatedRemainingBatches;
                
                Console.WriteLine($"Members Batch {batchNumber}: {membersBatch.Count} members in {batchTime:F2}s");
                Console.WriteLine($"Progress: {processedMemberCount}/{totalMembers} members ({(processedMemberCount * 100.0 / totalMembers):F2}%) - Est. remaining: {TimeSpan.FromSeconds(estimatedRemainingTime):hh\\:mm\\:ss}");
            }
            
            lastMemberId = membersBatch.Last().Id;
        }
        
        var membersTime = (DateTime.UtcNow - startTime).TotalSeconds;
        Console.WriteLine($"Phase 1 completed in {TimeSpan.FromSeconds(membersTime):hh\\:mm\\:ss}: {processedMemberCount} members migrated");

        // Phase 2: Migrate bundles and update member documents
        Console.WriteLine("Phase 2: Migrating bundles and updating member documents...");
        
        var bundlesStartTime = DateTime.UtcNow;
        var processedBundleCount = 0;
        long? lastBundleId = null;
        
        // Group bundles by member to prepare bulk updates
        var bundleUpdateBuffer = new Dictionary<Guid, List<Models.MongoDB.BundleEmbedded>>();
        const int updateBatchSize = 100; // Update 100 members at a time
        
        while (true)
        {
            var batchStartTime = DateTime.UtcNow;
            
            var bundlesBatch = await _postgreSqlRepository.GetBundlesBatchAsync(lastBundleId, _settings.BatchSize);
            
            if (!bundlesBatch.Any())
            {
                break;
            }
            
            // Convert bundles in parallel
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _settings.MaxDegreeOfParallelism };
            var bundlesByMember = new System.Collections.Concurrent.ConcurrentDictionary<Guid, System.Collections.Concurrent.ConcurrentBag<Models.MongoDB.BundleEmbedded>>();
            
            Parallel.ForEach(bundlesBatch, parallelOptions, bundle =>
            {
                var bundleEmbedded = DataConverter.ConvertToBundleEmbedded(bundle);
                var memberBundles = bundlesByMember.GetOrAdd(bundle.MemberId, _ => new System.Collections.Concurrent.ConcurrentBag<Models.MongoDB.BundleEmbedded>());
                memberBundles.Add(bundleEmbedded);
            });
            
            // Add to update buffer
            foreach (var kvp in bundlesByMember)
            {
                if (!bundleUpdateBuffer.ContainsKey(kvp.Key))
                {
                    bundleUpdateBuffer[kvp.Key] = new List<Models.MongoDB.BundleEmbedded>();
                }
                bundleUpdateBuffer[kvp.Key].AddRange(kvp.Value);
            }
            
            processedBundleCount += bundlesBatch.Count;
            
            // Perform bulk updates when buffer reaches threshold
            if (bundleUpdateBuffer.Count >= updateBatchSize)
            {
                await UpdateMemberBundlesAsync(membersCollection, bundleUpdateBuffer);
                bundleUpdateBuffer.Clear();
            }
            
            var batchTime = (DateTime.UtcNow - batchStartTime).TotalSeconds;
            var elapsedTime = (DateTime.UtcNow - bundlesStartTime).TotalSeconds;
            var batchNumber = processedBundleCount / _settings.BatchSize;
            var avgTimePerBatch = batchNumber > 0 ? elapsedTime / batchNumber : batchTime;
            var estimatedRemainingBatches = (totalBundles - processedBundleCount) / (double)_settings.BatchSize;
            var estimatedRemainingTime = avgTimePerBatch * estimatedRemainingBatches;
            
            Console.WriteLine($"Bundles Batch {batchNumber}: {bundlesBatch.Count} bundles in {batchTime:F2}s");
            Console.WriteLine($"Progress: {processedBundleCount}/{totalBundles} bundles ({(processedBundleCount * 100.0 / totalBundles):F2}%) - Est. remaining: {TimeSpan.FromSeconds(estimatedRemainingTime):hh\\:mm\\:ss}");
            
            lastBundleId = bundlesBatch.Last().Id;
        }
        
        // Update remaining members in buffer
        if (bundleUpdateBuffer.Any())
        {
            await UpdateMemberBundlesAsync(membersCollection, bundleUpdateBuffer);
        }
        
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

    private async Task UpdateMemberBundlesAsync(
        IMongoCollection<Models.MongoDB.MemberDocumentEmbedding> collection, 
        Dictionary<Guid, List<Models.MongoDB.BundleEmbedded>> bundlesByMemberId)
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
            await collection.BulkWriteAsync(bulkOps, new BulkWriteOptions { IsOrdered = false });
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

        // Migrate members
        Console.WriteLine("Starting members migration with cursor pagination...");
        
        var processedMemberCount = 0;
        Guid? lastMemberId = null;
        
        while (true)
        {
            var batchStartTime = DateTime.UtcNow;
            
            var membersBatch = await _postgreSqlRepository.GetMembersBatchAsync(lastMemberId, _settings.BatchSize);
            
            if (!membersBatch.Any())
            {
                break;
            }
            
            // Convert to MongoDB documents in parallel for better CPU utilization
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _settings.MaxDegreeOfParallelism };
            var documents = new System.Collections.Concurrent.ConcurrentBag<Models.MongoDB.MemberDocument>();
            
            Parallel.ForEach(membersBatch, parallelOptions, member =>
            {
                var document = DataConverter.ConvertToMemberDocument(member);
                documents.Add(document);
            });
            
            var documentsList = documents.ToList();

            if (documentsList.Any())
            {
                var options = new InsertManyOptions { IsOrdered = false };
                await membersCollection.InsertManyAsync(documentsList, options);
                processedMemberCount += documentsList.Count;
                
                var batchTime = (DateTime.UtcNow - batchStartTime).TotalSeconds;
                var elapsedTime = (DateTime.UtcNow - startTime).TotalSeconds;
                var batchNumber = processedMemberCount / _settings.BatchSize;
                var avgTimePerBatch = batchNumber > 0 ? elapsedTime / batchNumber : batchTime;
                var estimatedRemainingBatches = (totalMembers - processedMemberCount) / (double)_settings.BatchSize;
                var estimatedRemainingTime = avgTimePerBatch * estimatedRemainingBatches;
                
                Console.WriteLine($"Batch {batchNumber}: {membersBatch.Count} members in {batchTime:F2}s");
                Console.WriteLine($"Progress: {processedMemberCount}/{totalMembers} members ({(processedMemberCount * 100.0 / totalMembers):F2}%) - Est. remaining: {TimeSpan.FromSeconds(estimatedRemainingTime):hh\\:mm\\:ss}");
            }
            
            // Update cursor to the last member ID in this batch
            lastMemberId = membersBatch.Last().Id;
        }

        var membersMigrationTime = (DateTime.UtcNow - startTime).TotalSeconds;
        Console.WriteLine($"Members migration completed in {TimeSpan.FromSeconds(membersMigrationTime):hh\\:mm\\:ss}: {processedMemberCount} members migrated");

        // Migrate bundles
        Console.WriteLine("Starting bundles migration with cursor pagination...");
        
        var bundlesStartTime = DateTime.UtcNow;
        var processedBundleCount = 0;
        long? lastBundleId = null;
        
        while (true)
        {
            var batchStartTime = DateTime.UtcNow;
            
            var bundlesBatch = await _postgreSqlRepository.GetBundlesBatchAsync(lastBundleId, _settings.BatchSize);
            
            if (!bundlesBatch.Any())
            {
                break;
            }
            
            // Convert to MongoDB documents in parallel for better CPU utilization
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _settings.MaxDegreeOfParallelism };
            var documents = new System.Collections.Concurrent.ConcurrentBag<Models.MongoDB.BundleDocument>();
            
            Parallel.ForEach(bundlesBatch, parallelOptions, bundle =>
            {
                var document = DataConverter.ConvertToBundleDocument(bundle);
                documents.Add(document);
            });
            
            var documentsList = documents.ToList();

            if (documentsList.Any())
            {
                var options = new InsertManyOptions { IsOrdered = false };
                await bundlesCollection.InsertManyAsync(documentsList, options);
                processedBundleCount += documentsList.Count;
                
                var batchTime = (DateTime.UtcNow - batchStartTime).TotalSeconds;
                var elapsedTime = (DateTime.UtcNow - bundlesStartTime).TotalSeconds;
                var batchNumber = processedBundleCount / _settings.BatchSize;
                var avgTimePerBatch = batchNumber > 0 ? elapsedTime / batchNumber : batchTime;
                var estimatedRemainingBatches = (totalBundles - processedBundleCount) / (double)_settings.BatchSize;
                var estimatedRemainingTime = avgTimePerBatch * estimatedRemainingBatches;
                
                Console.WriteLine($"Batch {batchNumber}: {bundlesBatch.Count} bundles in {batchTime:F2}s");
                Console.WriteLine($"Progress: {processedBundleCount}/{totalBundles} bundles ({(processedBundleCount * 100.0 / totalBundles):F2}%) - Est. remaining: {TimeSpan.FromSeconds(estimatedRemainingTime):hh\\:mm\\:ss}");
            }
            
            // Update cursor to the last bundle ID in this batch
            lastBundleId = bundlesBatch.Last().Id;
        }
        
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
}
