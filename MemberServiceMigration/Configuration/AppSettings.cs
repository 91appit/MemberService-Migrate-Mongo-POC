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
}

public enum MigrationMode
{
    Embedding,
    Referencing
}
