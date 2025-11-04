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
        
        Console.WriteLine("Skipping index creation (will create after migration for better performance)...");

        Console.WriteLine("Starting batch migration with cursor pagination...");
        
        var processedMemberCount = 0;
        var processedBundleCount = 0;
        Guid? lastMemberId = null;
        var startTime = DateTime.UtcNow;
        
        while (true)
        {
            var batchStartTime = DateTime.UtcNow;
            
            // Fetch a batch of members using cursor pagination
            var membersBatch = await _postgreSqlRepository.GetMembersBatchAsync(lastMemberId, _settings.BatchSize);
            
            if (!membersBatch.Any())
            {
                break;
            }
            
            var membersReadTime = DateTime.UtcNow;
            
            // Fetch bundles for this batch of members
            var memberIds = membersBatch.Select(m => m.Id).ToList();
            var bundlesByMember = await _postgreSqlRepository.GetBundlesByMemberIdsAsync(memberIds);
            
            var batchBundleCount = bundlesByMember.Values.Sum(b => b.Count);
            var bundlesReadTime = DateTime.UtcNow;
            
            // Convert to MongoDB documents in parallel for better CPU utilization
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _settings.MaxDegreeOfParallelism };
            var documents = new System.Collections.Concurrent.ConcurrentBag<Models.MongoDB.MemberDocumentEmbedding>();
            
            Parallel.ForEach(membersBatch, parallelOptions, member =>
            {
                bundlesByMember.TryGetValue(member.Id, out var bundles);
                var document = DataConverter.ConvertToMemberDocumentEmbedding(member, bundles);
                documents.Add(document);
            });

            var conversionTime = DateTime.UtcNow;

            // Insert into MongoDB with unordered bulk insert for better performance
            var documentsList = documents.ToList();
            if (documentsList.Any())
            {
                var options = new InsertManyOptions { IsOrdered = false };
                await membersCollection.InsertManyAsync(documentsList, options);
                processedMemberCount += documentsList.Count;
                processedBundleCount += batchBundleCount;
                
                var insertTime = DateTime.UtcNow;
                var batchTotalTime = (insertTime - batchStartTime).TotalSeconds;
                var membersReadSec = (membersReadTime - batchStartTime).TotalSeconds;
                var bundlesReadSec = (bundlesReadTime - membersReadTime).TotalSeconds;
                var conversionSec = (conversionTime - bundlesReadTime).TotalSeconds;
                var insertSec = (insertTime - conversionTime).TotalSeconds;
                
                var elapsedTime = (DateTime.UtcNow - startTime).TotalSeconds;
                var avgTimePerBatch = elapsedTime / (processedMemberCount / (double)_settings.BatchSize);
                var estimatedRemainingBatches = (totalMembers - processedMemberCount) / (double)_settings.BatchSize;
                var estimatedRemainingTime = avgTimePerBatch * estimatedRemainingBatches;
                
                Console.WriteLine($"Batch {processedMemberCount / _settings.BatchSize}: {membersBatch.Count} members, {batchBundleCount} bundles in {batchTotalTime:F2}s (Read M:{membersReadSec:F2}s, B:{bundlesReadSec:F2}s, Conv:{conversionSec:F2}s, Insert:{insertSec:F2}s)");
                Console.WriteLine($"Progress: {processedMemberCount}/{totalMembers} members ({(processedMemberCount * 100.0 / totalMembers):F2}%), {processedBundleCount}/{totalBundles} bundles ({(processedBundleCount * 100.0 / totalBundles):F2}%) - Est. remaining: {TimeSpan.FromSeconds(estimatedRemainingTime):hh\\:mm\\:ss}");
            }
            
            // Update cursor to the last member ID in this batch
            lastMemberId = membersBatch.Last().Id;
        }
        
        var migrationTime = (DateTime.UtcNow - startTime).TotalSeconds;
        Console.WriteLine($"Data migration completed in {TimeSpan.FromSeconds(migrationTime):hh\\:mm\\:ss}: {processedMemberCount} members with {processedBundleCount} bundles migrated");
        
        Console.WriteLine("Creating indexes...");
        var indexStartTime = DateTime.UtcNow;
        await _mongoDbRepository.CreateIndexesForEmbeddingAsync();
        var indexTime = (DateTime.UtcNow - indexStartTime).TotalSeconds;
        Console.WriteLine($"Indexes created in {TimeSpan.FromSeconds(indexTime):hh\\:mm\\:ss}");
        
        var totalTime = (DateTime.UtcNow - startTime).TotalSeconds;
        Console.WriteLine($"Total migration time: {TimeSpan.FromSeconds(totalTime):hh\\:mm\\:ss}");
    }

    private async Task MigrateReferencingModeAsync()
    {
        Console.WriteLine("Counting records in PostgreSQL...");
        
        var totalMembers = await _postgreSqlRepository.GetMembersCountAsync();
        var totalBundles = await _postgreSqlRepository.GetBundlesCountAsync();
        
        Console.WriteLine($"Found {totalMembers} members and {totalBundles} bundles to migrate");

        var membersCollection = _mongoDbRepository.GetMembersCollection();
        var bundlesCollection = _mongoDbRepository.GetBundlesCollection();
        
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
                var avgTimePerBatch = elapsedTime / (processedMemberCount / (double)_settings.BatchSize);
                var estimatedRemainingBatches = (totalMembers - processedMemberCount) / (double)_settings.BatchSize;
                var estimatedRemainingTime = avgTimePerBatch * estimatedRemainingBatches;
                
                Console.WriteLine($"Batch {processedMemberCount / _settings.BatchSize}: {membersBatch.Count} members in {batchTime:F2}s");
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
                var avgTimePerBatch = elapsedTime / (processedBundleCount / (double)_settings.BatchSize);
                var estimatedRemainingBatches = (totalBundles - processedBundleCount) / (double)_settings.BatchSize;
                var estimatedRemainingTime = avgTimePerBatch * estimatedRemainingBatches;
                
                Console.WriteLine($"Batch {processedBundleCount / _settings.BatchSize}: {bundlesBatch.Count} bundles in {batchTime:F2}s");
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
