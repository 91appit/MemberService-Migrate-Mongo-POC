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
