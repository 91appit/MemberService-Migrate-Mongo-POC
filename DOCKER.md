# Docker Setup for MongoDB

This guide explains how to use Docker to set up a MongoDB instance for the migration tool.

## Prerequisites

- [Docker](https://docs.docker.com/get-docker/) installed
- [Docker Compose](https://docs.docker.com/compose/install/) installed

## Quick Start

### 1. Start MongoDB with Docker Compose

```bash
docker-compose up -d
```

This command will:
- Start a MongoDB 7.0 instance on port 27017
- Start Mongo Express (web-based MongoDB admin interface) on port 8081
- Create persistent volumes for MongoDB data

### 2. Verify MongoDB is Running

```bash
docker-compose ps
```

You should see both `memberservice-mongodb` and `memberservice-mongo-express` containers running.

### 3. Access MongoDB

**Via Connection String:**
```
mongodb://admin:admin123@localhost:27017/memberdb?authSource=admin
```

**Via Mongo Express (Web UI):**
- Open browser to: http://localhost:8081
- Username: `admin`
- Password: `admin123`

### 4. Update Application Configuration

Update your `appsettings.json` with the Docker MongoDB connection string:

```json
{
  "Database": {
    "PostgreSqlConnectionString": "Host=localhost;Database=memberdb;Username=postgres;Password=your_password",
    "MongoDbConnectionString": "mongodb://admin:admin123@localhost:27017/memberdb?authSource=admin",
    "MongoDbDatabaseName": "memberdb"
  },
  "Migration": {
    "Mode": "Embedding",
    "BatchSize": 1000
  }
}
```

### 5. Run the Migration

```bash
dotnet run --project MemberServiceMigration
```

## Docker Compose Services

### MongoDB
- **Image**: mongo:7.0
- **Port**: 27017
- **Username**: admin
- **Password**: admin123
- **Database**: memberdb
- **Data Volume**: mongodb_data (persistent storage)

### Mongo Express
- **Image**: mongo-express:latest
- **Port**: 8081 (Web UI)
- **Username**: admin
- **Password**: admin123
- **Purpose**: Web-based MongoDB administration interface

## Common Commands

### Start services
```bash
docker-compose up -d
```

### Stop services
```bash
docker-compose down
```

### Stop services and remove volumes (⚠️ deletes all data)
```bash
docker-compose down -v
```

### View logs
```bash
# All services
docker-compose logs -f

# MongoDB only
docker-compose logs -f mongodb

# Mongo Express only
docker-compose logs -f mongo-express
```

### Restart services
```bash
docker-compose restart
```

### Check service status
```bash
docker-compose ps
```

## Data Persistence

MongoDB data is stored in a Docker volume named `mongodb_data`. This ensures your data persists even if you stop or restart the containers.

To backup the data:
```bash
docker run --rm -v mongodb_data:/data -v $(pwd):/backup mongo:7.0 tar czf /backup/mongodb_backup.tar.gz -C /data .
```

To restore the data:
```bash
docker run --rm -v mongodb_data:/data -v $(pwd):/backup mongo:7.0 tar xzf /backup/mongodb_backup.tar.gz -C /data
```

## Accessing MongoDB Shell

To access the MongoDB shell inside the container:

```bash
docker exec -it memberservice-mongodb mongosh -u admin -p admin123 --authenticationDatabase admin
```

Once inside, you can run MongoDB commands:
```javascript
// Switch to memberdb database
use memberdb

// Show collections
show collections

// Count documents in members collection
db.members.countDocuments()

// Find a sample member
db.members.findOne()
```

## Customization

### Change MongoDB Port

Edit `docker-compose.yml` and change the port mapping:
```yaml
ports:
  - "27018:27017"  # Change 27017 to your preferred port
```

### Change Credentials

Edit `docker-compose.yml` and update the environment variables:
```yaml
environment:
  MONGO_INITDB_ROOT_USERNAME: your_username
  MONGO_INITDB_ROOT_PASSWORD: your_password
```

Don't forget to update your `appsettings.json` connection string accordingly.

### Disable Mongo Express

If you don't need the web UI, comment out or remove the `mongo-express` service in `docker-compose.yml`.

## Troubleshooting

### Port Already in Use

If port 27017 or 8081 is already in use:
```bash
# Check what's using the port
lsof -i :27017
lsof -i :8081

# Stop the conflicting service or change the port in docker-compose.yml
```

### Container Won't Start

Check the logs:
```bash
docker-compose logs mongodb
```

### Connection Refused

Make sure:
1. MongoDB container is running: `docker-compose ps`
2. You're using the correct connection string with authentication
3. Firewall isn't blocking the port

### Reset Everything

To completely reset and start fresh:
```bash
docker-compose down -v
docker-compose up -d
```

## Production Considerations

⚠️ **Important**: This Docker setup is designed for development and testing purposes.

For production use, consider:
- Using stronger passwords
- Enabling SSL/TLS connections
- Implementing MongoDB replica sets
- Using MongoDB authentication with specific user roles
- Implementing proper backup strategies
- Using environment variables or secrets management for credentials
- Configuring resource limits
- Setting up monitoring and alerting

## Network Configuration

If you need to connect from another Docker container, use the network name:
```
mongodb://admin:admin123@mongodb:27017/memberdb?authSource=admin
```

Note: Use `mongodb` (service name) instead of `localhost` when connecting from other containers in the same Docker network.

## Additional Resources

- [MongoDB Docker Official Documentation](https://hub.docker.com/_/mongo)
- [Mongo Express Documentation](https://github.com/mongo-express/mongo-express)
- [Docker Compose Documentation](https://docs.docker.com/compose/)
