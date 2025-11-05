using System.Text.Json;

namespace MemberServiceMigration.Models;

public class Bundle
{
    public long Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public int Type { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public JsonDocument? Extensions { get; set; }
    public Guid MemberId { get; set; }
}
