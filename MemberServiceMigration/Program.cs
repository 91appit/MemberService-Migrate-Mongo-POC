using Microsoft.Extensions.Configuration;
using MemberServiceMigration.Configuration;
using MemberServiceMigration.Database;
using MemberServiceMigration.Migration;

Console.WriteLine("=== Member Service Migration Tool ===");
Console.WriteLine();

var exitCode = 0;

try
{
    var configuration = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .Build();

    var appSettings = new AppSettings();
    configuration.Bind(appSettings);

    Console.WriteLine($"PostgreSQL Connection: {MaskConnectionString(appSettings.Database.PostgreSqlConnectionString)}");
    Console.WriteLine($"MongoDB Connection: {MaskConnectionString(appSettings.Database.MongoDbConnectionString)}");
    Console.WriteLine($"MongoDB Database: {appSettings.Database.MongoDbDatabaseName}");
    Console.WriteLine($"MongoDB Members Collection: {appSettings.Database.MembersCollectionName}");
    Console.WriteLine($"MongoDB Bundles Collection: {appSettings.Database.BundlesCollectionName}");
    Console.WriteLine($"Migration Mode: {appSettings.Migration.Mode}");
    Console.WriteLine($"Batch Size: {appSettings.Migration.BatchSize}");
    Console.WriteLine($"Max Degree of Parallelism: {appSettings.Migration.MaxDegreeOfParallelism}");
    Console.WriteLine($"Concurrent Batch Processors: {appSettings.Migration.ConcurrentBatchProcessors}");
    Console.WriteLine($"Max Channel Capacity: {appSettings.Migration.MaxChannelCapacity}");
    Console.WriteLine($"Checkpoint Enabled: {appSettings.Migration.EnableCheckpoint}");
    if (appSettings.Migration.EnableCheckpoint)
    {
        Console.WriteLine($"Checkpoint File: {appSettings.Migration.CheckpointFilePath}");
        Console.WriteLine($"Checkpoint Interval: Every {appSettings.Migration.CheckpointInterval} batches");
    }
    Console.WriteLine();

    var postgreSqlRepository = new PostgreSqlRepository(appSettings.Database.PostgreSqlConnectionString);
    var mongoDbRepository = new MongoDbRepository(
        appSettings.Database.MongoDbConnectionString,
        appSettings.Database.MongoDbDatabaseName,
        appSettings.Database.MembersCollectionName,
        appSettings.Database.BundlesCollectionName);

    var migrationService = new MigrationService(
        postgreSqlRepository,
        mongoDbRepository,
        appSettings.Migration);

    await migrationService.MigrateAsync();
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine($"Stack trace: {ex.StackTrace}");
    exitCode = 1;
}

Console.WriteLine();
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
return exitCode;

static string MaskConnectionString(string connectionString)
{
    if (string.IsNullOrEmpty(connectionString))
        return "Not configured";

    var parts = connectionString.Split(';');
    var maskedParts = parts.Select(part =>
    {
        if (part.Contains("Password=", StringComparison.OrdinalIgnoreCase) ||
            part.Contains("Pwd=", StringComparison.OrdinalIgnoreCase))
        {
            var keyValue = part.Split('=');
            return keyValue.Length == 2 ? $"{keyValue[0]}=****" : part;
        }
        return part;
    });

    return string.Join(";", maskedParts);
}
