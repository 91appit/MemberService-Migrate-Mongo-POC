# Quick Start Guide

This guide will help you get started with the Member Service Migration Tool quickly.

## Prerequisites

Before you begin, ensure you have the following installed:

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- Access to a PostgreSQL database with the source data
- **Option A**: [Docker](https://docs.docker.com/get-docker/) and [Docker Compose](https://docs.docker.com/compose/install/) (recommended)
- **Option B**: Access to a MongoDB instance (local or cloud-based)

## Step 1: Clone the Repository

```bash
git clone <repository-url>
cd MemberService-Migrate-Mongo-POC
```

## Step 2: Set Up MongoDB with Docker (Recommended)

The easiest way to get started is using Docker:

```bash
# Start MongoDB and Mongo Express
docker-compose up -d

# Verify containers are running
docker-compose ps
```

This will start:
- MongoDB on port 27017
- Mongo Express (Web UI) on port 8081 (http://localhost:8081)

Default credentials:
- Username: `admin`
- Password: `admin123`

For more details, see [DOCKER.md](DOCKER.md)

## Step 3: Configure the Application

1. Navigate to the application directory:
   ```bash
   cd MemberServiceMigration
   ```

2. Copy the example configuration:
   ```bash
   cp appsettings.example.json appsettings.json
   ```

3. Edit `appsettings.json` with your database connection details:
   ```json
   {
     "Database": {
       "PostgreSqlConnectionString": "Host=your_host;Port=5432;Database=your_db;Username=your_user;Password=your_password",
       "MongoDbConnectionString": "mongodb://admin:admin123@localhost:27017/memberdb?authSource=admin",
       "MongoDbDatabaseName": "memberdb"
     },
     "Migration": {
       "Mode": "Embedding",
       "BatchSize": 1000
     }
   }
   ```

### Connection String Examples

**PostgreSQL:**
```
Host=localhost;Port=5432;Database=memberdb;Username=postgres;Password=mypassword
```

**MongoDB (Docker - default):**
```
mongodb://admin:admin123@localhost:27017/memberdb?authSource=admin
```

**MongoDB (Local without auth):**
```
mongodb://localhost:27017
```

**MongoDB (Cloud - Atlas):**
```
mongodb+srv://username:password@cluster.mongodb.net/?retryWrites=true&w=majority
```

## Step 4: Build the Application

```bash
dotnet build MemberServiceMigration.sln
```

## Step 5: Run the Migration

```bash
dotnet run --project MemberServiceMigration
```

The application will:
1. Connect to both PostgreSQL and MongoDB
2. Read all members and bundles from PostgreSQL
3. Convert the data to MongoDB format
4. Create appropriate indexes in MongoDB
5. Insert the data in batches
6. Display progress and completion messages

## Step 6: Verify the Migration

### Using Mongo Express (Web UI)

1. Open http://localhost:8081 in your browser
2. Login with username `admin` and password `admin123`
3. Select the `memberdb` database
4. View the `members` collection (and `bundles` if using Referencing mode)

### Using MongoDB Shell

Connect to MongoDB:
```bash
docker exec -it memberservice-mongodb mongosh -u admin -p admin123 --authenticationDatabase admin
```

### For Embedding Mode

Check the members collection:

```javascript
// Using MongoDB Shell
use memberdb

// Count documents
db.members.countDocuments()

// View a sample document
db.members.findOne()

// Check that bundles are embedded
db.members.findOne({}, { bundles: 1 })
```

### For Referencing Mode

Check both collections:

```javascript
// Using MongoDB Shell
use memberdb

// Count members
db.members.countDocuments()

// Count bundles
db.bundles.countDocuments()

// View sample documents
db.members.findOne()
db.bundles.findOne()

// Verify the relationship
const member = db.members.findOne()
db.bundles.find({ member_id: member._id })
```

## Switching Between Modes

To switch from Embedding to Referencing mode (or vice versa):

1. Update `appsettings.json`:
   ```json
   {
     "Migration": {
       "Mode": "Referencing",
       "BatchSize": 1000
     }
   }
   ```

2. **Important**: Drop the existing MongoDB collections before running the migration again:
   ```javascript
   // In MongoDB Shell
   use memberdb
   db.members.drop()
   db.bundles.drop()  // Only needed if it exists
   ```

3. Run the migration again:
   ```bash
   dotnet run
   ```

## Troubleshooting

### Connection Issues

**PostgreSQL Connection Failed:**
- Verify the host, port, database name, username, and password
- Ensure PostgreSQL is running and accessible
- Check firewall settings
- Verify the user has read permissions on the tables

**MongoDB Connection Failed:**
- Verify the connection string format
- Ensure MongoDB is running and accessible
- Check network connectivity
- For cloud databases, verify IP whitelist settings

### Migration Errors

**"Table does not exist" Error:**
- Ensure the PostgreSQL database has the `members` and `bundles` tables
- Verify the database name in the connection string
- The required schema is documented in the main [README.md](README.md) under "PostgreSQL Schema"

**Important**: Your PostgreSQL database must have the following tables:
- `members` table with columns: id, password, salt, tenant_id, state, allow_login, extensions, create_at, create_user, update_at, update_user, version, tags, profile, tags_v2
- `bundles` table with columns: id, key, type, tenant_id, extensions, member_id, create_at, create_user, update_at, update_user

See the complete DDL in the [README.md](README.md) for exact schema definitions.

**"Out of Memory" Error:**
- Reduce the `BatchSize` in `appsettings.json` (try 100 or 500)
- Ensure your system has sufficient memory

**"Document too large" Error (Embedding Mode):**
- This means some members have too many bundles
- Consider using Referencing mode instead
- Or filter out members with excessive bundles

### Data Validation

Check data counts match:

```bash
# PostgreSQL
psql -U postgres -d memberdb -c "SELECT COUNT(*) FROM members;"
psql -U postgres -d memberdb -c "SELECT COUNT(*) FROM bundles;"

# MongoDB (using mongosh)
mongosh --eval "use memberdb; db.members.countDocuments()"
mongosh --eval "use memberdb; db.bundles.countDocuments()"  # Referencing mode only
```

## Performance Tips

1. **Batch Size**: Adjust based on your system resources
   - Small batches (100-500): Lower memory usage, more overhead
   - Large batches (1000-5000): Higher memory usage, better performance

2. **Network Latency**: 
   - Run the migration tool close to your databases (same network/region)
   - For cloud migrations, consider running in the same cloud region

3. **Index Creation**:
   - Indexes are created before data insertion
   - This is optimal for bulk inserts
   - For very large datasets, you might want to create indexes after insertion

4. **Monitoring**:
   - Watch the console output for progress
   - Monitor memory usage during migration
   - Check database CPU and disk I/O

## Next Steps

- Read [MIGRATION_MODES.md](MIGRATION_MODES.md) for detailed comparison of migration modes
- Review the [README.md](README.md) for complete documentation
- Customize the code for your specific requirements
- Add validation and data transformation logic as needed

## Support

For issues or questions:
- Check the troubleshooting section above
- Review the code in the repository
- Create an issue in the GitHub repository

## Security Notes

⚠️ **Important Security Considerations:**

- Never commit `appsettings.json` with real credentials to version control
- Use environment variables or secure vaults for production credentials
- Ensure database users have minimum required permissions
- Use SSL/TLS for database connections in production
- Review and sanitize any sensitive data before migration

## Clean Up

After successful migration and verification:

1. **Backup** your MongoDB data
2. Keep the PostgreSQL data until you're certain the migration is successful
3. Plan for the decommissioning of the old system

---

**Happy Migrating! 🚀**
