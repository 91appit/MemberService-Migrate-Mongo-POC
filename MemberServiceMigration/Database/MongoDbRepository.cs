using MongoDB.Driver;
using MemberServiceMigration.Models.MongoDB;

namespace MemberServiceMigration.Database;

public class MongoDbRepository
{
    private readonly IMongoDatabase _database;

    public MongoDbRepository(string connectionString, string databaseName)
    {
        var client = new MongoClient(connectionString);
        _database = client.GetDatabase(databaseName);
    }

    public IMongoCollection<MemberDocumentEmbedding> GetMembersEmbeddingCollection()
    {
        return _database.GetCollection<MemberDocumentEmbedding>("members");
    }

    public IMongoCollection<MemberDocument> GetMembersCollection()
    {
        return _database.GetCollection<MemberDocument>("members");
    }

    public IMongoCollection<BundleDocument> GetBundlesCollection()
    {
        return _database.GetCollection<BundleDocument>("bundles");
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
                bundleIndexKeys.Ascending(b => b.UpdateAt),
                new CreateIndexOptions { Name = "ix_bundles_update_at" }
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
        await _database.DropCollectionAsync("members");
    }

    public async Task DropMembersCollectionAsync()
    {
        await _database.DropCollectionAsync("members");
    }

    public async Task DropBundlesCollectionAsync()
    {
        await _database.DropCollectionAsync("bundles");
    }
}
