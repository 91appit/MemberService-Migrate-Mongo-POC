# Checkpoint and Resume Feature

## Overview

The migration tool now supports checkpoint and resume functionality, allowing you to resume a migration from the last processed record if the migration is interrupted (either accidentally or manually).

## How It Works

The checkpoint feature automatically saves migration progress at configurable intervals. When the migration is interrupted and restarted, it can resume from the last saved checkpoint instead of starting from the beginning.

### What is Saved

The checkpoint file stores:
- **Last Member ID**: The ID of the last successfully migrated member
- **Last Bundle ID**: The ID of the last successfully migrated bundle
- **Migration Mode**: The mode (Embedding or Referencing) used during migration
- **Timestamp**: When the checkpoint was last updated
- **Status**: Current phase of the migration (e.g., "Phase1-MigratingMembers", "Phase2-MigratingBundles")

### Checkpoint File

By default, the checkpoint is saved to `migration_checkpoint.json` in the application's working directory. This file is automatically:
- Created when migration starts
- Updated periodically during migration (configurable interval)
- Deleted when migration completes successfully

The checkpoint file is excluded from version control (added to `.gitignore`).

## Configuration

Configure the checkpoint feature in `appsettings.json`:

```json
{
  "Migration": {
    "EnableCheckpoint": true,
    "CheckpointFilePath": "migration_checkpoint.json",
    "CheckpointInterval": 10
  }
}
```

### Configuration Parameters

- **EnableCheckpoint** (boolean, default: `true`)
  - Enables or disables the checkpoint feature
  - Set to `false` if you want to disable checkpoint functionality

- **CheckpointFilePath** (string, default: `"migration_checkpoint.json"`)
  - Path to the checkpoint file
  - Can be relative or absolute path
  - File will be created automatically if it doesn't exist

- **CheckpointInterval** (integer, default: `10`)
  - How often to save checkpoints (in number of batches)
  - For example, `10` means save a checkpoint every 10 batches
  - Lower values save more frequently (more I/O overhead but less data loss on interruption)
  - Higher values save less frequently (less I/O overhead but more data loss on interruption)

## Usage

### Starting a Fresh Migration

When you start the migration tool with no existing checkpoint:

```bash
dotnet run --project MemberServiceMigration
```

The migration will start from the beginning and create checkpoints as it progresses.

### Resuming from an Interruption

If the migration was interrupted and you restart it:

```bash
dotnet run --project MemberServiceMigration
```

The tool will detect the existing checkpoint and prompt you:

```
=== Existing Checkpoint Found ===
Mode: Embedding
Last Member ID: 12345678-1234-1234-1234-123456789012
Last Bundle ID: 98765
Timestamp: 2025-01-07 10:30:45
Status: Phase2-MigratingBundles

Do you want to resume from the checkpoint? (Y/N, default: Y):
```

- Press **Y** (or just Enter) to resume from the checkpoint
- Press **N** to start fresh (this will delete the checkpoint and drop existing data)

### Starting Fresh When a Checkpoint Exists

If you want to ignore an existing checkpoint and start fresh:

1. When prompted, enter **N**
2. Or manually delete the checkpoint file before running
3. Or disable checkpoints in `appsettings.json`

## Migration Phases

### Embedding Mode

The checkpoint tracks two phases:

1. **Phase 1 - Migrating Members**
   - Status: `"Phase1-MigratingMembers"` → `"Phase1-Completed"`
   - Tracks: `LastMemberId`
   - Members are migrated first without bundles

2. **Phase 2 - Migrating Bundles**
   - Status: `"Phase2-MigratingBundles"`
   - Tracks: `LastBundleId`
   - Bundles are migrated and embedded into member documents

### Referencing Mode

The checkpoint tracks two separate migrations:

1. **Migrating Members**
   - Status: `"MigratingMembers"` → `"MembersCompleted"`
   - Tracks: `LastMemberId`
   - Members are migrated to the members collection

2. **Migrating Bundles**
   - Status: `"MigratingBundles"`
   - Tracks: `LastBundleId`
   - Bundles are migrated to the bundles collection

## Best Practices

### Checkpoint Interval Tuning

Choose an appropriate checkpoint interval based on your needs:

- **High-frequency checkpoints (e.g., every 5 batches)**
  - Pros: Minimal data loss if interrupted
  - Cons: More I/O operations, slightly slower migration
  - Recommended for: Long-running migrations, unstable environments

- **Medium-frequency checkpoints (e.g., every 10-20 batches)**
  - Pros: Good balance between safety and performance
  - Cons: Some data re-processing on interruption
  - Recommended for: Most use cases (default)

- **Low-frequency checkpoints (e.g., every 50+ batches)**
  - Pros: Minimal performance impact
  - Cons: More data re-processing if interrupted
  - Recommended for: Fast, reliable environments

### Monitoring Progress

The application logs checkpoint saves to the console. You'll see:
- Checkpoint file location on startup
- Periodic updates showing progress
- Checkpoint information when resuming

### Handling Failures

If the migration fails:
1. The last checkpoint is preserved
2. Review error messages to diagnose the issue
3. Fix the underlying problem (database connectivity, data issues, etc.)
4. Restart the migration - it will resume from the last checkpoint

### Changing Migration Mode

If you need to change the migration mode (e.g., from Embedding to Referencing):
1. The tool will warn you if the checkpoint mode doesn't match the current configuration
2. Either:
   - Continue with the checkpoint mode (press Y)
   - Start fresh with the new mode (press N)

## Troubleshooting

### Checkpoint File Corruption

If the checkpoint file becomes corrupted:
```
Warning: Failed to load checkpoint: [error message]
```

Solution: Delete the checkpoint file and start fresh.

### Checkpoint Not Saving

If checkpoints aren't being saved:
1. Check `EnableCheckpoint` is set to `true` in `appsettings.json`
2. Verify the application has write permissions to the checkpoint file location
3. Check disk space availability

### Resume Starting from Wrong Position

If resume starts from an unexpected position:
1. The checkpoint uses cursor-based pagination (`id > last_id`)
2. Ensure the PostgreSQL tables haven't changed since the last checkpoint
3. If data was modified, consider starting fresh

### Performance Impact

The checkpoint feature has minimal performance impact:
- Checkpoint saves are "fire and forget" (non-blocking)
- I/O operations are infrequent (configurable interval)
- JSON serialization is fast for small checkpoint data

Typical overhead: < 1% of total migration time

## Disabling Checkpoints

To completely disable the checkpoint feature:

```json
{
  "Migration": {
    "EnableCheckpoint": false
  }
}
```

When disabled:
- No checkpoint file is created
- No progress is saved
- Migration always starts from the beginning
- Useful for: testing, small datasets, or when guaranteed uninterrupted execution

## Security Considerations

The checkpoint file contains:
- ✅ Last processed IDs (not sensitive)
- ✅ Migration mode and status (not sensitive)
- ✅ Timestamp (not sensitive)
- ❌ No database credentials
- ❌ No actual data content

The file is safe to share for debugging purposes, but still recommended to keep internal.
