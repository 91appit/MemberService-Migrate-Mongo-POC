using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MemberServiceMigration.Models.MongoDB;

public class MemberDocumentEmbedding : MemberDocument
{
    [BsonElement("bundles")]
    [BsonIgnoreIfNull]
    public List<BundleEmbedded>? Bundles { get; set; }
}

public class BundleEmbedded
{
    [BsonElement("id")]
    public long Id { get; set; }
    
    [BsonElement("key")]
    public string Key { get; set; } = string.Empty;
    
    [BsonElement("type")]
    public int Type { get; set; }
    
    [BsonElement("extensions")]
    [BsonIgnoreIfNull]
    public BsonDocument? Extensions { get; set; }
    
    [BsonElement("create_at")]
    public DateTime CreateAt { get; set; }
    
    [BsonElement("create_user")]
    [BsonIgnoreIfNull]
    public string? CreateUser { get; set; }
    
    [BsonElement("update_at")]
    public DateTime UpdateAt { get; set; }
    
    [BsonElement("update_user")]
    [BsonIgnoreIfNull]
    public string? UpdateUser { get; set; }
}
