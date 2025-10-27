using System.Text.Json;

namespace MemberServiceMigration.Models;

public class Member
{
    public Guid Id { get; set; }
    public string? Password { get; set; }
    public string? Salt { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public int State { get; set; }
    public bool AllowLogin { get; set; }
    public JsonDocument? Extensions { get; set; }
    public DateTime CreateAt { get; set; }
    public string? CreateUser { get; set; }
    public DateTime UpdateAt { get; set; }
    public string? UpdateUser { get; set; }
    public int Version { get; set; }
    public string[]? Tags { get; set; }
    public JsonDocument? Profile { get; set; }
    public JsonDocument? TagsV2 { get; set; }
}
