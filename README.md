"# MemberService-Migrate-Mongo-POC

A .NET 8 Console application for migrating member and bundle data from PostgreSQL to MongoDB with support for both Embedding and Referencing data structure patterns.

## Background and Motivation

This application is designed to migrate data from a PostgreSQL database to MongoDB to leverage MongoDB's flexible document-based data structure. The migration supports two different data structure patterns to ensure optimal system performance, maintainability, and scalability.

## Features

- **Two Migration Modes**:
  - **Embedding Mode**: Embeds bundles data directly into member documents
  - **Referencing Mode**: Maintains separate collections for members and bundles with references
- **Cursor-Based Pagination**: 
  - Uses efficient cursor pagination (`id > last_id`) instead of OFFSET/LIMIT
  - Consistent query performance regardless of dataset size or position
  - Leverages primary key indexes for optimal speed
- **Batch Processing**: 
  - Configurable batch size for efficient data migration
  - Batch reading from PostgreSQL to handle large datasets (millions of records)
  - Batch writing to MongoDB for optimal performance
- **Memory Efficient**: Processes data in chunks without loading entire dataset into memory
- **Index Creation**: Automatically creates appropriate indexes in MongoDB
- **Data Integrity**: Ensures data consistency during migration
- **Progress Tracking**: Real-time progress reporting with percentage completion
- **Error Handling**: Comprehensive error handling and logging

## Project Structure

```
MemberServiceMigration/
├── Configuration/          # Application settings and configuration
│   └── AppSettings.cs
├── Database/              # Database connection modules
│   ├── PostgreSqlRepository.cs
│   └── MongoDbRepository.cs
├── Migration/             # Migration logic
│   ├── DataConverter.cs
│   └── MigrationService.cs
├── Models/                # Data models
│   ├── Member.cs
│   ├── Bundle.cs
│   └── MongoDB/           # MongoDB document models
│       ├── MemberDocument.cs
│       ├── MemberDocumentEmbedding.cs
│       └── BundleDocument.cs
├── appsettings.json       # Configuration file
└── Program.cs             # Application entry point
```

## Prerequisites

- .NET 8 SDK
- Visual Studio 2022 or later (optional, for GUI development)
- PostgreSQL database with `members` and `bundles` tables
- MongoDB instance (or use Docker - see below)

## Quick Start with Docker

The easiest way to set up MongoDB for this project is using Docker:

```bash
# Start MongoDB and Mongo Express
docker-compose up -d

# Verify containers are running
docker-compose ps
```

MongoDB will be available at: `mongodb://admin:admin123@localhost:27017/memberdb?authSource=admin`

Mongo Express (Web UI) will be available at: http://localhost:8081

For detailed Docker setup instructions, see [DOCKER.md](DOCKER.md)

## Opening the Project

### Using Visual Studio
1. Open `MemberServiceMigration.sln` in Visual Studio
2. The solution will automatically restore NuGet packages
3. Press F5 to run or Ctrl+Shift+B to build

### Using Command Line
```bash
dotnet build MemberServiceMigration.sln
dotnet run --project MemberServiceMigration
```

## Configuration

Edit `appsettings.json` to configure the application:

```json
{
  "Database": {
    "PostgreSqlConnectionString": "Host=localhost;Database=memberdb;Username=postgres;Password=postgres",
    "MongoDbConnectionString": "mongodb://localhost:27017",
    "MongoDbDatabaseName": "memberdb"
  },
  "Migration": {
    "Mode": "Embedding",  // or "Referencing"
    "BatchSize": 1000
  }
}
```

### Migration Modes

#### Embedding Mode

In this mode, bundles data is embedded directly into the member documents:

```json
{
  "_id": "uuid-of-member",
  "password": "...",
  "tenant_id": "...",
  "state": 1,
  "allow_login": true,
  "profile": {...},
  "tags": ["tag1", "tag2"],
  "bundles": [
    {
      "id": 1,
      "key": "bundle1",
      "type": 0,
      "extensions": {...}
    }
  ]
}
```

**Use when:**
- Bundles are always accessed with their parent member
- Number of bundles per member is relatively small
- Query pattern primarily focuses on member data

#### Referencing Mode

In this mode, bundles are stored in a separate collection with references to members:

```json
// members collection
{
  "_id": "uuid-of-member",
  "password": "...",
  "tenant_id": "...",
  "state": 1,
  "allow_login": true,
  "profile": {...},
  "tags": ["tag1", "tag2"]
}

// bundles collection
{
  "_id": 1,
  "key": "bundle1",
  "type": 0,
  "tenant_id": "...",
  "member_id": "uuid-of-member",
  "extensions": {...}
}
```

**Use when:**
- Bundles are frequently accessed independently
- Number of bundles per member can be very large
- Query patterns require searching bundles directly

## Usage

### Using Visual Studio
1. Open `MemberServiceMigration.sln` in Visual Studio
2. Update the configuration in `appsettings.json`
3. Press F5 to run the application

### Using Command Line
1. **Build the project:**
   ```bash
   dotnet build MemberServiceMigration.sln
   ```

2. **Update the configuration in `appsettings.json`**

3. **Run the migration:**
   ```bash
   dotnet run --project MemberServiceMigration
   ```

## PostgreSQL Schema

The application expects the following PostgreSQL tables:

### Members Table

```sql
CREATE TABLE public.members (
    id uuid NOT NULL,
    password text NULL,
    salt text NULL,
    tenant_id varchar(60) NOT NULL,
    state int4 NOT NULL,
    allow_login bool NOT NULL,
    extensions jsonb NULL,
    create_at timestamptz NOT NULL,
    create_user varchar(50) NULL,
    update_at timestamptz NOT NULL,
    update_user varchar(50) NULL,
    version int4 NOT NULL,
    tags _text NULL,
    profile jsonb NULL,
    tags_v2 jsonb NULL,
    CONSTRAINT pk_members PRIMARY KEY (id)
);
```

### Bundles Table

```sql
CREATE TABLE public.bundles (
    id int8 GENERATED BY DEFAULT AS IDENTITY NOT NULL,
    key varchar(60) NOT NULL,
    type int4 NOT NULL,
    tenant_id varchar(60) NOT NULL,
    extensions jsonb NULL,
    member_id uuid NOT NULL,
    create_at timestamptz NOT NULL,
    create_user varchar(50) NULL,
    update_at timestamptz NOT NULL,
    update_user varchar(50) NULL,
    CONSTRAINT pk_bundles PRIMARY KEY (id),
    CONSTRAINT fk_bundles_members_member_id FOREIGN KEY (member_id) 
        REFERENCES public.members(id) ON DELETE CASCADE
);
```

## MongoDB Indexes

The application automatically creates the following indexes:

### Embedding Mode

On `members` collection:
- `ix_members_tenant_id` on `tenant_id`
- `ix_members_update_at` on `update_at`
- `ix_members_tags` on `tags`

### Referencing Mode

On `members` collection:
- `ix_members_tenant_id` on `tenant_id`
- `ix_members_update_at` on `update_at`
- `ix_members_tags` on `tags`

On `bundles` collection:
- `ix_bundles_member_id` on `member_id`
- `ix_bundles_tenant_id` on `tenant_id`
- `ix_bundles_update_at` on `update_at`
- `ix_bundles_key_tenant_id_type` (unique) on `key`, `tenant_id`, `type`

## Technology Stack

- **Language**: C# (.NET 8)
- **Project Type**: Console Application
- **Database Drivers**:
  - PostgreSQL: Npgsql
  - MongoDB: MongoDB.Driver
- **Configuration**: Microsoft.Extensions.Configuration

## Error Handling

The application includes comprehensive error handling:
- Database connection errors
- Data conversion errors
- Batch processing errors
- Detailed error messages and stack traces

## Performance Optimization

This migration tool has been optimized for large-scale data migrations (millions of records). See [PERFORMANCE_OPTIMIZATION.md](PERFORMANCE_OPTIMIZATION.md) for detailed information on:

- Performance bottlenecks and solutions
- Expected performance improvements (65-70% faster)
- Configuration tuning guide
- Monitoring and troubleshooting
- Real-time performance metrics

**Expected Performance:**
- Before: ~3 hours for 5M members + 20M bundles
- After: ~55-60 minutes for 5M members + 20M bundles

## Non-Goals

This POC initially did not include extensive performance optimization, but has been enhanced with:
- ✅ Deferred index creation for faster inserts
- ✅ Unordered bulk inserts for parallel writes
- ✅ Connection pooling optimization
- ✅ Parallel data conversion
- ✅ Real-time performance monitoring

Additional non-goals:
- MongoDB high availability or sharding configuration
- Database backup and recovery strategies
- Production-grade alerting systems

## License

This is a proof-of-concept project for internal use.
" 
