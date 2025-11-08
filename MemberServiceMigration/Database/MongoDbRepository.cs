using MongoDB.Driver;
using MemberServiceMigration.Models.MongoDB;

namespace MemberServiceMigration.Database;

public class MongoDbRepository
{
    private readonly IMongoDatabase _database;
    private readonly string _membersCollectionName;
    private readonly string _bundlesCollectionName;

    public MongoDbRepository(string connectionString, string databaseName, string membersCollectionName = "prod_members", string bundlesCollectionName = "bundles")
    {
        _membersCollectionName = membersCollectionName;
        _bundlesCollectionName = bundlesCollectionName;
        
        // Configure MongoDB client settings for concurrent operations and reliability
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        
        // Increase connection pool size to handle concurrent batch processors
        settings.MaxConnectionPoolSize = 200;
        settings.MinConnectionPoolSize = 10;
        
        // Increase timeouts to prevent premature connection closures during large batch operations
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(30);
        settings.ConnectTimeout = TimeSpan.FromSeconds(30);
        settings.SocketTimeout = TimeSpan.FromMinutes(5);
        
        // Enable retry on network errors
        settings.RetryWrites = true;
        settings.RetryReads = true;
        
        var client = new MongoClient(settings);
        _database = client.GetDatabase(databaseName);
    }

    public IMongoCollection<MemberDocumentEmbedding> GetMembersEmbeddingCollection()
    {
        return _database.GetCollection<MemberDocumentEmbedding>(_membersCollectionName);
    }

    public IMongoCollection<MemberDocument> GetMembersCollection()
    {
        return _database.GetCollection<MemberDocument>(_membersCollectionName);
    }

    public IMongoCollection<BundleDocument> GetBundlesCollection()
    {
        return _database.GetCollection<BundleDocument>(_bundlesCollectionName);
    }

    public async Task CreateIndexesForEmbeddingAsync()
    {
        var membersCollection = GetMembersEmbeddingCollection();
        
        var indexKeys = Builders<MemberDocumentEmbedding>.IndexKeys;
        
        await membersCollection.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<MemberDocumentEmbedding>(
                indexKeys.Ascending(m => m.TenantId),
                new CreateIndexOptions { Name = "ix_members_tenant_id" }
            ),
            new CreateIndexModel<MemberDocumentEmbedding>(
                indexKeys.Ascending(m => m.UpdateAt),
                new CreateIndexOptions { Name = "ix_members_update_at" }
            ),
            new CreateIndexModel<MemberDocumentEmbedding>(
                indexKeys.Ascending(m => m.Tags),
                new CreateIndexOptions { Name = "ix_members_tags" }
            )
        });
    }

    public async Task CreateIndexesForReferencingAsync()
    {
        var membersCollection = GetMembersCollection();
        var bundlesCollection = GetBundlesCollection();
        
        var memberIndexKeys = Builders<MemberDocument>.IndexKeys;
        var bundleIndexKeys = Builders<BundleDocument>.IndexKeys;
        
        await membersCollection.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<MemberDocument>(
                memberIndexKeys.Ascending(m => m.TenantId),
                new CreateIndexOptions { Name = "ix_members_tenant_id" }
            ),
            new CreateIndexModel<MemberDocument>(
                memberIndexKeys.Ascending(m => m.UpdateAt),
                new CreateIndexOptions { Name = "ix_members_update_at" }
            ),
            new CreateIndexModel<MemberDocument>(
                memberIndexKeys.Ascending(m => m.Tags),
                new CreateIndexOptions { Name = "ix_members_tags" }
            )
        });
        
        await bundlesCollection.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<BundleDocument>(
                bundleIndexKeys.Ascending(b => b.MemberId),
                new CreateIndexOptions { Name = "ix_bundles_member_id" }
            ),
            new CreateIndexModel<BundleDocument>(
                bundleIndexKeys.Ascending(b => b.TenantId),
                new CreateIndexOptions { Name = "ix_bundles_tenant_id" }
            ),
            new CreateIndexModel<BundleDocument>(
                bundleIndexKeys.Combine(
                    bundleIndexKeys.Ascending(b => b.Key),
                    bundleIndexKeys.Ascending(b => b.TenantId),
                    bundleIndexKeys.Ascending(b => b.Type)
                ),
                new CreateIndexOptions { Name = "ix_bundles_key_tenant_id_type", Unique = true }
            )
        });
    }

    public async Task DropMembersEmbeddingCollectionAsync()
    {
        await _database.DropCollectionAsync(_membersCollectionName);
    }

    public async Task DropMembersCollectionAsync()
    {
        await _database.DropCollectionAsync(_membersCollectionName);
    }

    public async Task DropBundlesCollectionAsync()
    {
        await _database.DropCollectionAsync(_bundlesCollectionName);
    }
}
