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
        Console.WriteLine($"Found {totalMembers} members to migrate");

        var membersCollection = _mongoDbRepository.GetMembersEmbeddingCollection();
        
        Console.WriteLine("Creating indexes...");
        await _mongoDbRepository.CreateIndexesForEmbeddingAsync();

        Console.WriteLine("Starting batch migration...");
        
        var processedCount = 0;
        var offset = 0;
        
        while (offset < totalMembers)
        {
            Console.WriteLine($"Fetching batch at offset {offset}...");
            
            // Fetch a batch of members
            var membersBatch = await _postgreSqlRepository.GetMembersBatchAsync(offset, _settings.BatchSize);
            
            if (!membersBatch.Any())
            {
                break;
            }
            
            // Fetch bundles for this batch of members
            var memberIds = membersBatch.Select(m => m.Id).ToList();
            var bundlesByMember = await _postgreSqlRepository.GetBundlesByMemberIdsAsync(memberIds);
            
            Console.WriteLine($"Converting {membersBatch.Count} members with their bundles...");
            
            // Convert to MongoDB documents
            var documents = membersBatch.Select(member =>
            {
                bundlesByMember.TryGetValue(member.Id, out var bundles);
                return DataConverter.ConvertToMemberDocumentEmbedding(member, bundles);
            }).ToList();

            // Insert into MongoDB
            if (documents.Any())
            {
                await membersCollection.InsertManyAsync(documents);
                processedCount += documents.Count;
                Console.WriteLine($"Processed {processedCount}/{totalMembers} members ({(processedCount * 100.0 / totalMembers):F2}%)");
            }
            
            offset += _settings.BatchSize;
        }
        
        Console.WriteLine($"Migration completed: {processedCount} members migrated");
    }

    private async Task MigrateReferencingModeAsync()
    {
        Console.WriteLine("Counting records in PostgreSQL...");
        
        var totalMembers = await _postgreSqlRepository.GetMembersCountAsync();
        var totalBundles = await _postgreSqlRepository.GetBundlesCountAsync();
        
        Console.WriteLine($"Found {totalMembers} members and {totalBundles} bundles to migrate");

        var membersCollection = _mongoDbRepository.GetMembersCollection();
        var bundlesCollection = _mongoDbRepository.GetBundlesCollection();
        
        Console.WriteLine("Creating indexes...");
        await _mongoDbRepository.CreateIndexesForReferencingAsync();

        // Migrate members
        Console.WriteLine("Starting members migration...");
        
        var processedMemberCount = 0;
        var memberOffset = 0;
        
        while (memberOffset < totalMembers)
        {
            Console.WriteLine($"Fetching members batch at offset {memberOffset}...");
            
            var membersBatch = await _postgreSqlRepository.GetMembersBatchAsync(memberOffset, _settings.BatchSize);
            
            if (!membersBatch.Any())
            {
                break;
            }
            
            Console.WriteLine($"Converting {membersBatch.Count} members...");
            
            var documents = membersBatch.Select(DataConverter.ConvertToMemberDocument).ToList();

            if (documents.Any())
            {
                await membersCollection.InsertManyAsync(documents);
                processedMemberCount += documents.Count;
                Console.WriteLine($"Processed {processedMemberCount}/{totalMembers} members ({(processedMemberCount * 100.0 / totalMembers):F2}%)");
            }
            
            memberOffset += _settings.BatchSize;
        }

        // Migrate bundles
        Console.WriteLine("Starting bundles migration...");
        
        var processedBundleCount = 0;
        var bundleOffset = 0;
        
        while (bundleOffset < totalBundles)
        {
            Console.WriteLine($"Fetching bundles batch at offset {bundleOffset}...");
            
            var bundlesBatch = await _postgreSqlRepository.GetBundlesBatchAsync(bundleOffset, _settings.BatchSize);
            
            if (!bundlesBatch.Any())
            {
                break;
            }
            
            Console.WriteLine($"Converting {bundlesBatch.Count} bundles...");
            
            var documents = bundlesBatch.Select(DataConverter.ConvertToBundleDocument).ToList();

            if (documents.Any())
            {
                await bundlesCollection.InsertManyAsync(documents);
                processedBundleCount += documents.Count;
                Console.WriteLine($"Processed {processedBundleCount}/{totalBundles} bundles ({(processedBundleCount * 100.0 / totalBundles):F2}%)");
            }
            
            bundleOffset += _settings.BatchSize;
        }
        
        Console.WriteLine($"Migration completed: {processedMemberCount} members and {processedBundleCount} bundles migrated");
    }
}
