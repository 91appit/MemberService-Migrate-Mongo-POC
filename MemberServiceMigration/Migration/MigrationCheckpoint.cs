using MemberServiceMigration.Configuration;

namespace MemberServiceMigration.Migration;

public class MigrationCheckpoint
{
    public MigrationMode Mode { get; set; }
    public Guid? LastMemberId { get; set; }
    public long? LastBundleId { get; set; }
    public DateTime Timestamp { get; set; }
    public string Status { get; set; } = "InProgress";
}
