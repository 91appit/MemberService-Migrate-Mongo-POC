using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MemberServiceMigration.Models.MongoDB;

public class BundleDocument
{
    [BsonId]
    public long Id { get; set; }
    
    [BsonElement("key")]
    public string Key { get; set; } = string.Empty;
    
    [BsonElement("type")]
    public int Type { get; set; }
    
    [BsonElement("tenant_id")]
    public string TenantId { get; set; } = string.Empty;
    
    [BsonElement("extensions")]
    [BsonIgnoreIfNull]
    public BsonDocument? Extensions { get; set; }
    
    [BsonElement("member_id")]
    [BsonRepresentation(BsonType.String)]
    public Guid MemberId { get; set; }
}
