# Migration Modes Comparison

This document provides a detailed comparison of the two migration modes supported by the Member Service Migration Tool.

## Overview

The migration tool supports two different approaches for structuring data in MongoDB:

1. **Embedding Mode**: Embeds bundles directly within member documents
2. **Referencing Mode**: Maintains separate collections with document references

## Embedding Mode

### Structure

In Embedding mode, all bundles belonging to a member are stored as an array within the member document:

```json
{
  "_id": "550e8400-e29b-41d4-a716-446655440000",
  "password": "hashed_password",
  "salt": "salt_value",
  "tenant_id": "tenant_001",
  "state": 1,
  "allow_login": true,
  "profile": {
    "name": "John Doe",
    "email": "john@example.com"
  },
  "tags": ["premium", "active"],
  "bundles": [
    {
      "id": 1,
      "key": "bundle_key_1",
      "type": 0,
      "extensions": {
        "feature_1": true
      },
      "create_at": "2024-01-01T00:00:00Z",
      "update_at": "2024-01-01T00:00:00Z"
    },
    {
      "id": 2,
      "key": "bundle_key_2",
      "type": 1,
      "extensions": {
        "feature_2": false
      },
      "create_at": "2024-01-02T00:00:00Z",
      "update_at": "2024-01-02T00:00:00Z"
    }
  ],
  "create_at": "2024-01-01T00:00:00Z",
  "update_at": "2024-01-01T00:00:00Z",
  "version": 1
}
```

### Collections Created

- `members`: Contains member documents with embedded bundles

### Indexes Created

On `members` collection:
- `ix_members_tenant_id`: For filtering by tenant
- `ix_members_update_at`: For temporal queries
- `ix_members_tags`: For tag-based queries

### Advantages

1. **Single Query Access**: Retrieve member and all their bundles in one query
2. **Better Performance**: No need for joins or additional lookups
3. **Data Locality**: Related data is stored together, improving read performance
4. **Atomic Updates**: All data can be updated in a single atomic operation
5. **Simpler Queries**: No need to manage relationships between collections

### Disadvantages

1. **Document Size Limit**: MongoDB has a 16MB document size limit
2. **Duplication**: If bundles need to be accessed independently, there might be duplication
3. **Update Complexity**: Updating individual bundles requires updating the entire document
4. **Growing Arrays**: Documents grow over time as bundles are added

### Best Use Cases

- Members have a relatively small, fixed number of bundles
- Bundles are always accessed in the context of their parent member
- Application primarily performs member-centric queries
- Strong consistency between member and bundle data is required
- Total data size per member (including bundles) is well under 16MB

### Query Examples

```javascript
// Get member with all bundles
// Note: _id is stored as a string representation of the UUID
db.members.findOne({ _id: "550e8400-e29b-41d4-a716-446655440000" })

// Get members with specific bundle type
db.members.find({ "bundles.type": 0 })

// Update a specific bundle
db.members.updateOne(
  { 
    _id: "550e8400-e29b-41d4-a716-446655440000",
    "bundles.id": 1
  },
  {
    $set: { "bundles.$.key": "new_key" }
  }
)
```

## Referencing Mode

### Structure

In Referencing mode, members and bundles are stored in separate collections:

**Members Collection:**
```json
{
  "_id": "550e8400-e29b-41d4-a716-446655440000",
  "password": "hashed_password",
  "salt": "salt_value",
  "tenant_id": "tenant_001",
  "state": 1,
  "allow_login": true,
  "profile": {
    "name": "John Doe",
    "email": "john@example.com"
  },
  "tags": ["premium", "active"],
  "create_at": "2024-01-01T00:00:00Z",
  "update_at": "2024-01-01T00:00:00Z",
  "version": 1
}
```

**Bundles Collection:**
```json
{
  "_id": 1,
  "key": "bundle_key_1",
  "type": 0,
  "tenant_id": "tenant_001",
  "member_id": "550e8400-e29b-41d4-a716-446655440000",
  "extensions": {
    "feature_1": true
  },
  "create_at": "2024-01-01T00:00:00Z",
  "update_at": "2024-01-01T00:00:00Z"
}
```

### Collections Created

- `members`: Contains member documents
- `bundles`: Contains bundle documents with references to members

### Indexes Created

On `members` collection:
- `ix_members_tenant_id`: For filtering by tenant
- `ix_members_update_at`: For temporal queries
- `ix_members_tags`: For tag-based queries

On `bundles` collection:
- `ix_bundles_member_id`: For looking up bundles by member
- `ix_bundles_tenant_id`: For filtering by tenant
- `ix_bundles_update_at`: For temporal queries
- `ix_bundles_key_tenant_id_type` (unique, compound): Composite index on (key, tenant_id, type) to ensure unique bundle keys per tenant and type combination

### Advantages

1. **No Size Limits**: Each document can grow independently
2. **Independent Access**: Bundles can be queried and updated independently
3. **Flexible Updates**: Individual bundles can be updated without affecting the member document
4. **Better for Large Datasets**: More efficient when members have many bundles
5. **Reduced Duplication**: Bundles can be referenced from multiple places if needed

### Disadvantages

1. **Multiple Queries**: Requires multiple queries or $lookup to get related data
2. **Slower Reads**: Join operations are more expensive than embedded documents
3. **Consistency Challenges**: Maintaining referential integrity across collections
4. **Complex Queries**: More complex query patterns for related data

### Best Use Cases

- Members can have a large or unbounded number of bundles
- Bundles are frequently accessed or modified independently
- Application needs to query bundles across multiple members
- Bundle data is large and would cause documents to approach size limits
- Different parts of the application work with members and bundles separately

### Query Examples

```javascript
// Get member
const member = db.members.findOne({ _id: "550e8400-e29b-41d4-a716-446655440000" })

// Get bundles for a member (separate query)
const bundles = db.bundles.find({ member_id: "550e8400-e29b-41d4-a716-446655440000" })

// Get member with bundles using aggregation
db.members.aggregate([
  { $match: { _id: "550e8400-e29b-41d4-a716-446655440000" } },
  {
    $lookup: {
      from: "bundles",
      localField: "_id",
      foreignField: "member_id",
      as: "bundles"
    }
  }
])

// Update a specific bundle (simple direct update)
db.bundles.updateOne(
  { _id: 1 },
  { $set: { key: "new_key" } }
)

// Find all bundles of a specific type across all members
db.bundles.find({ type: 0 })
```

## Performance Comparison

| Aspect | Embedding | Referencing |
|--------|-----------|-------------|
| Read Performance (member + bundles) | Excellent (single query) | Good (requires join) |
| Write Performance (add bundle) | Moderate (document update) | Excellent (single insert) |
| Write Performance (update bundle) | Moderate (array update) | Excellent (direct update) |
| Write Performance (remove bundle) | Moderate (array operation) | Excellent (direct delete) |
| Storage Efficiency | Good (some overhead) | Better (no duplication) |
| Scalability | Limited by document size | Highly scalable |
| Query Complexity | Simple | More complex |

## Migration Tool Configuration

To switch between modes, update the `appsettings.json`:

```json
{
  "Migration": {
    "Mode": "Embedding",  // or "Referencing"
    "BatchSize": 1000
  }
}
```

### Batch Size Considerations

The `BatchSize` setting controls how many records are:
1. **Read from PostgreSQL** in each query (using OFFSET/LIMIT)
2. **Written to MongoDB** in each batch operation

**For Large Datasets (Millions of Records):**
- Recommended: 1000-5000 records per batch
- The tool uses streaming approach - fetches one batch, processes it, writes to MongoDB, then fetches the next
- Memory usage stays constant regardless of total dataset size
- Progress is reported with percentage completion

**Memory Efficiency:**
- Old behavior: Loaded ALL records into memory before processing ❌
- New behavior: Processes records in batches, only current batch in memory ✅
- Supports datasets of any size (tested with tens of millions of records)

## Recommendations

### Choose Embedding When:
- ✅ One-to-few relationships (< 100 bundles per member)
- ✅ Data is always accessed together
- ✅ Atomic operations are critical
- ✅ Read performance is the priority
- ✅ Total document size is predictable and small

### Choose Referencing When:
- ✅ One-to-many relationships (potentially many bundles per member)
- ✅ Independent access patterns
- ✅ Write-heavy workloads on bundles
- ✅ Need to query bundles across members
- ✅ Document size could grow unbounded

## Performance at Scale

The migration tool is optimized for large-scale migrations:

- **Batch Reading**: Uses PostgreSQL OFFSET/LIMIT to fetch records in chunks
- **Batch Writing**: Inserts multiple documents to MongoDB in single operations
- **Memory Efficient**: Processes one batch at a time, never loads entire dataset
- **Progress Tracking**: Real-time percentage updates during migration
- **Scalability**: Successfully handles tens of millions of records

**Example Output for Large Dataset:**
```
Starting migration in Embedding mode...
Batch size: 1000
Counting records in PostgreSQL...
Found 15000000 members to migrate
Creating indexes...
Starting batch migration...
Fetching batch at offset 0...
Converting 1000 members with their bundles...
Processed 1000/15000000 members (0.01%)
Fetching batch at offset 1000...
...
Processed 15000000/15000000 members (100.00%)
Migration completed: 15000000 members migrated
```

## Conclusion

Both modes have their place depending on your specific use case. The Embedding mode provides better read performance and simpler queries for tightly coupled data, while the Referencing mode offers better scalability and flexibility for larger, more independent datasets.

Consider your access patterns, data size, and update frequency when choosing the appropriate mode for your application.
