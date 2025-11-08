namespace MemberServiceMigration.Configuration;

public class AppSettings
{
    public DatabaseSettings Database { get; set; } = new();
    public MigrationSettings Migration { get; set; } = new();
}

public class DatabaseSettings
{
    public string PostgreSqlConnectionString { get; set; } = string.Empty;
    public string MongoDbConnectionString { get; set; } = string.Empty;
    public string MongoDbDatabaseName { get; set; } = string.Empty;
    public string MembersCollectionName { get; set; } = "prod_members";
    public string BundlesCollectionName { get; set; } = "bundles";
}

public class MigrationSettings
{
    public MigrationMode Mode { get; set; } = MigrationMode.Embedding;
    public int BatchSize { get; set; } = 1000;
    public int MaxDegreeOfParallelism { get; set; } = 4;
    public int ConcurrentBatchProcessors { get; set; } = 3;
    public int MaxChannelCapacity { get; set; } = 10;
    public bool EnableCheckpoint { get; set; } = true;
    public string CheckpointFilePath { get; set; } = "migration_checkpoint.json";
    public int CheckpointInterval { get; set; } = 10;
    
    // Parallel query settings
    public int ParallelMemberProducers { get; set; } = 1;
    public int ParallelBundleProducers { get; set; } = 1;
    
    // Custom member partition boundaries (optional)
    // If not specified, partitions will be created by dividing time range evenly
    // Example: ["2024-01-01", "2025-01-01"] creates 3 partitions:
    //   Partition 0: min to 2024-01-01
    //   Partition 1: 2024-01-01 to 2025-01-01
    //   Partition 2: 2025-01-01 to max
    public List<string>? MemberPartitionBoundaries { get; set; } = null;
}

public enum MigrationMode
{
    Embedding,
    Referencing
}
