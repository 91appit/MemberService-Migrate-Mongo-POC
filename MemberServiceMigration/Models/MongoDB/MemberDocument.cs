using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MemberServiceMigration.Models.MongoDB;

public class MemberDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; }
    
    [BsonElement("password")]
    public string? Password { get; set; }
    
    [BsonElement("salt")]
    public string? Salt { get; set; }
    
    [BsonElement("tenant_id")]
    public string TenantId { get; set; } = string.Empty;
    
    [BsonElement("state")]
    public int State { get; set; }
    
    [BsonElement("allow_login")]
    public bool AllowLogin { get; set; }
    
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
    
    [BsonElement("version")]
    public int Version { get; set; }
    
    [BsonElement("tags")]
    [BsonIgnoreIfNull]
    public string[]? Tags { get; set; }
    
    [BsonElement("profile")]
    [BsonIgnoreIfNull]
    public BsonDocument? Profile { get; set; }
    
    [BsonElement("tags_v2")]
    [BsonIgnoreIfNull]
    public BsonDocument? TagsV2 { get; set; }
}
