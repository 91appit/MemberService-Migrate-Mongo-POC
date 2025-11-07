using System.Text.Json;
using MemberServiceMigration.Configuration;

namespace MemberServiceMigration.Migration;

public class CheckpointService
{
    private readonly string _checkpointFilePath;

    public CheckpointService(string checkpointFilePath)
    {
        _checkpointFilePath = checkpointFilePath;
    }

    public async Task<MigrationCheckpoint?> LoadCheckpointAsync()
    {
        if (!File.Exists(_checkpointFilePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_checkpointFilePath);
            return JsonSerializer.Deserialize<MigrationCheckpoint>(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to load checkpoint: {ex.Message}");
            return null;
        }
    }

    public async Task SaveCheckpointAsync(MigrationCheckpoint checkpoint)
    {
        try
        {
            var json = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            await File.WriteAllTextAsync(_checkpointFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to save checkpoint: {ex.Message}");
        }
    }

    public void DeleteCheckpoint()
    {
        try
        {
            if (File.Exists(_checkpointFilePath))
            {
                File.Delete(_checkpointFilePath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to delete checkpoint: {ex.Message}");
        }
    }

    public bool CheckpointExists()
    {
        return File.Exists(_checkpointFilePath);
    }
}
