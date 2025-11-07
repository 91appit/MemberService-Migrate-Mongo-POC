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
    public List<string> UpdateAtPartitions { get; set; } = new();
}

public enum MigrationMode
{
    Embedding,
    Referencing
}
