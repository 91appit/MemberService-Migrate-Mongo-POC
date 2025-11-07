using MemberServiceMigration.Configuration;

namespace MemberServiceMigration.Migration;

public class MigrationCheckpoint
{
    public MigrationMode Mode { get; set; }
    public Guid? LastMemberId { get; set; }
    public long? LastBundleId { get; set; }
    public DateTime Timestamp { get; set; }
    public string Status { get; set; } = MigrationStatus.InProgress;
    public int? CurrentPartitionIndex { get; set; }
}

public static class MigrationStatus
{
    public const string InProgress = "InProgress";
    public const string Phase1MigratingMembers = "Phase1-MigratingMembers";
    public const string Phase1Completed = "Phase1-Completed";
    public const string Phase2MigratingBundles = "Phase2-MigratingBundles";
    public const string MigratingMembers = "MigratingMembers";
    public const string MembersCompleted = "MembersCompleted";
    public const string MigratingBundles = "MigratingBundles";
}
