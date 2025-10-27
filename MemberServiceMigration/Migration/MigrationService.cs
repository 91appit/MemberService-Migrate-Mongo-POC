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
        Console.WriteLine("Fetching data from PostgreSQL...");
        
        var members = await _postgreSqlRepository.GetAllMembersAsync();
        var bundlesByMember = await _postgreSqlRepository.GetBundlesByMemberIdAsync();
        
        Console.WriteLine($"Found {members.Count} members and {bundlesByMember.Values.Sum(b => b.Count)} bundles");

        var membersCollection = _mongoDbRepository.GetMembersEmbeddingCollection();
        
        Console.WriteLine("Creating indexes...");
        await _mongoDbRepository.CreateIndexesForEmbeddingAsync();

        Console.WriteLine("Converting and inserting data...");
        
        var batches = members
            .Select((member, index) => new { member, index })
            .GroupBy(x => x.index / _settings.BatchSize)
            .Select(g => g.Select(x => x.member).ToList());

        var processedCount = 0;
        foreach (var batch in batches)
        {
            var documents = batch.Select(member =>
            {
                bundlesByMember.TryGetValue(member.Id, out var bundles);
                return DataConverter.ConvertToMemberDocumentEmbedding(member, bundles);
            }).ToList();

            if (documents.Any())
            {
                await membersCollection.InsertManyAsync(documents);
                processedCount += documents.Count;
                Console.WriteLine($"Processed {processedCount}/{members.Count} members");
            }
        }
    }

    private async Task MigrateReferencingModeAsync()
    {
        Console.WriteLine("Fetching data from PostgreSQL...");
        
        var members = await _postgreSqlRepository.GetAllMembersAsync();
        var bundles = await _postgreSqlRepository.GetAllBundlesAsync();
        
        Console.WriteLine($"Found {members.Count} members and {bundles.Count} bundles");

        var membersCollection = _mongoDbRepository.GetMembersCollection();
        var bundlesCollection = _mongoDbRepository.GetBundlesCollection();
        
        Console.WriteLine("Creating indexes...");
        await _mongoDbRepository.CreateIndexesForReferencingAsync();

        Console.WriteLine("Converting and inserting members...");
        
        var memberBatches = members
            .Select((member, index) => new { member, index })
            .GroupBy(x => x.index / _settings.BatchSize)
            .Select(g => g.Select(x => x.member).ToList());

        var processedMemberCount = 0;
        foreach (var batch in memberBatches)
        {
            var documents = batch.Select(DataConverter.ConvertToMemberDocument).ToList();

            if (documents.Any())
            {
                await membersCollection.InsertManyAsync(documents);
                processedMemberCount += documents.Count;
                Console.WriteLine($"Processed {processedMemberCount}/{members.Count} members");
            }
        }

        Console.WriteLine("Converting and inserting bundles...");
        
        var bundleBatches = bundles
            .Select((bundle, index) => new { bundle, index })
            .GroupBy(x => x.index / _settings.BatchSize)
            .Select(g => g.Select(x => x.bundle).ToList());

        var processedBundleCount = 0;
        foreach (var batch in bundleBatches)
        {
            var documents = batch.Select(DataConverter.ConvertToBundleDocument).ToList();

            if (documents.Any())
            {
                await bundlesCollection.InsertManyAsync(documents);
                processedBundleCount += documents.Count;
                Console.WriteLine($"Processed {processedBundleCount}/{bundles.Count} bundles");
            }
        }
    }
}
