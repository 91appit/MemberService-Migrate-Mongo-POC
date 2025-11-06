using Npgsql;
using MemberServiceMigration.Models;
using MemberServiceMigration.Data;
using System.Text.Json;

namespace MemberServiceMigration.Database;

public class PostgreSqlRepository
{
    private readonly string _connectionString;
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlRepository(string connectionString)
    {
        _connectionString = connectionString;
        
        // Create a data source with optimized connection pooling for better performance
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = 100;
        dataSourceBuilder.ConnectionStringBuilder.MinPoolSize = 10;
        _dataSource = dataSourceBuilder.Build();
    }

    public async Task<long> GetMembersCountAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var query = "SELECT COUNT(*) FROM members";
        await using var command = new NpgsqlCommand(query, connection);
        
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    public async Task<(Guid? minId, Guid? maxId)> GetMembersIdRangeAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var query = "SELECT MIN(id), MAX(id) FROM members";
        await using var command = new NpgsqlCommand(query, connection);
        
        await using var reader = await command.ExecuteReaderAsync();
        
        if (await reader.ReadAsync())
        {
            var minId = reader.IsDBNull(0) ? (Guid?)null : reader.GetGuid(0);
            var maxId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
            return (minId, maxId);
        }
        
        return (null, null);
    }

    public async Task<long> GetBundlesCountAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var query = "SELECT COUNT(*) FROM bundles";
        await using var command = new NpgsqlCommand(query, connection);
        
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    public async Task<(long? minId, long? maxId)> GetBundlesIdRangeAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var query = "SELECT MIN(id), MAX(id) FROM bundles";
        await using var command = new NpgsqlCommand(query, connection);
        
        await using var reader = await command.ExecuteReaderAsync();
        
        if (await reader.ReadAsync())
        {
            var minId = reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
            var maxId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            return (minId, maxId);
        }
        
        return (null, null);
    }

    public async Task<List<Member>> GetMembersBatchAsync(Guid? lastMemberId, int limit)
    {
        var members = new List<Member>();
        
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        string query;
        if (lastMemberId.HasValue)
        {
            query = @"
                SELECT id, password, salt, tenant_id, state, allow_login, 
                       create_at, create_user, update_at, update_user, version
                FROM members
                WHERE id > @lastId
                ORDER BY id
                LIMIT @limit";
        }
        else
        {
            query = @"
                SELECT id, password, salt, tenant_id, state, allow_login, 
                       create_at, create_user, update_at, update_user, version
                FROM members
                ORDER BY id
                LIMIT @limit";
        }
        
        await using var command = new NpgsqlCommand(query, connection);
        if (lastMemberId.HasValue)
        {
            command.Parameters.AddWithValue("lastId", lastMemberId.Value);
        }
        command.Parameters.AddWithValue("limit", limit);
        
        await using var reader = await command.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            var member = new Member
            {
                Id = reader.GetGuid(0),
                Password = reader.IsDBNull(1) ? null : reader.GetString(1),
                Salt = reader.IsDBNull(2) ? null : reader.GetString(2),
                TenantId = reader.GetString(3),
                State = reader.GetInt32(4),
                AllowLogin = reader.GetBoolean(5),
                CreateAt = reader.GetDateTime(6),
                CreateUser = reader.IsDBNull(7) ? null : reader.GetString(7),
                UpdateAt = reader.GetDateTime(8),
                UpdateUser = reader.IsDBNull(9) ? null : reader.GetString(9),
                Version = reader.GetInt32(10),
                // Use mock data for sensitive fields
                Extensions = MockDataProvider.GetMemberExtension(),
                Tags = MockDataProvider.GetMemberTags(),
                Profile = MockDataProvider.GetMemberProfile(),
                TagsV2 = MockDataProvider.GetMemberTagsV2()
            };
            
            members.Add(member);
        }
        
        return members;
    }

    public async Task<List<Member>> GetMembersBatchInRangeAsync(Guid? lastMemberId, Guid? maxId, int limit)
    {
        var members = new List<Member>();
        
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        string query;
        if (lastMemberId.HasValue && maxId.HasValue)
        {
            query = @"
                SELECT id, password, salt, tenant_id, state, allow_login, 
                       create_at, create_user, update_at, update_user, version
                FROM members
                WHERE id > @lastId AND id <= @maxId
                ORDER BY id
                LIMIT @limit";
        }
        else if (maxId.HasValue)
        {
            query = @"
                SELECT id, password, salt, tenant_id, state, allow_login, 
                       create_at, create_user, update_at, update_user, version
                FROM members
                WHERE id <= @maxId
                ORDER BY id
                LIMIT @limit";
        }
        else if (lastMemberId.HasValue)
        {
            query = @"
                SELECT id, password, salt, tenant_id, state, allow_login, 
                       create_at, create_user, update_at, update_user, version
                FROM members
                WHERE id > @lastId
                ORDER BY id
                LIMIT @limit";
        }
        else
        {
            query = @"
                SELECT id, password, salt, tenant_id, state, allow_login, 
                       create_at, create_user, update_at, update_user, version
                FROM members
                ORDER BY id
                LIMIT @limit";
        }
        
        await using var command = new NpgsqlCommand(query, connection);
        if (lastMemberId.HasValue)
        {
            command.Parameters.AddWithValue("lastId", lastMemberId.Value);
        }
        if (maxId.HasValue)
        {
            command.Parameters.AddWithValue("maxId", maxId.Value);
        }
        command.Parameters.AddWithValue("limit", limit);
        
        await using var reader = await command.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            var member = new Member
            {
                Id = reader.GetGuid(0),
                Password = reader.IsDBNull(1) ? null : reader.GetString(1),
                Salt = reader.IsDBNull(2) ? null : reader.GetString(2),
                TenantId = reader.GetString(3),
                State = reader.GetInt32(4),
                AllowLogin = reader.GetBoolean(5),
                CreateAt = reader.GetDateTime(6),
                CreateUser = reader.IsDBNull(7) ? null : reader.GetString(7),
                UpdateAt = reader.GetDateTime(8),
                UpdateUser = reader.IsDBNull(9) ? null : reader.GetString(9),
                Version = reader.GetInt32(10),
                // Use mock data for sensitive fields
                Extensions = MockDataProvider.GetMemberExtension(),
                Tags = MockDataProvider.GetMemberTags(),
                Profile = MockDataProvider.GetMemberProfile(),
                TagsV2 = MockDataProvider.GetMemberTagsV2()
            };
            
            members.Add(member);
        }
        
        return members;
    }

    public async Task<List<Bundle>> GetBundlesBatchAsync(long? lastBundleId, int limit)
    {
        var bundles = new List<Bundle>();
        
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        string query;
        if (lastBundleId.HasValue)
        {
            query = @"
                SELECT id, type, tenant_id, member_id
                FROM bundles
                WHERE id > @lastId
                ORDER BY id
                LIMIT @limit";
        }
        else
        {
            query = @"
                SELECT id, type, tenant_id, member_id
                FROM bundles
                ORDER BY id
                LIMIT @limit";
        }
        
        await using var command = new NpgsqlCommand(query, connection);
        if (lastBundleId.HasValue)
        {
            command.Parameters.AddWithValue("lastId", lastBundleId.Value);
        }
        command.Parameters.AddWithValue("limit", limit);
        
        await using var reader = await command.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            var bundle = new Bundle
            {
                Id = reader.GetInt64(0),
                Type = reader.GetInt32(1),
                TenantId = reader.GetString(2),
                MemberId = reader.GetGuid(3),
                // Use mock data for sensitive fields
                Key = MockDataProvider.GetBundleKey(),
                Extensions = MockDataProvider.GetBundleExtension()
            };
            
            bundles.Add(bundle);
        }
        
        return bundles;
    }

    public async Task<List<Bundle>> GetBundlesBatchInRangeAsync(long? lastBundleId, long? maxId, int limit)
    {
        var bundles = new List<Bundle>();
        
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        string query;
        if (lastBundleId.HasValue && maxId.HasValue)
        {
            query = @"
                SELECT id, type, tenant_id, member_id
                FROM bundles
                WHERE id > @lastId AND id <= @maxId
                ORDER BY id
                LIMIT @limit";
        }
        else if (maxId.HasValue)
        {
            query = @"
                SELECT id, type, tenant_id, member_id
                FROM bundles
                WHERE id <= @maxId
                ORDER BY id
                LIMIT @limit";
        }
        else if (lastBundleId.HasValue)
        {
            query = @"
                SELECT id, type, tenant_id, member_id
                FROM bundles
                WHERE id > @lastId
                ORDER BY id
                LIMIT @limit";
        }
        else
        {
            query = @"
                SELECT id, type, tenant_id, member_id
                FROM bundles
                ORDER BY id
                LIMIT @limit";
        }
        
        await using var command = new NpgsqlCommand(query, connection);
        if (lastBundleId.HasValue)
        {
            command.Parameters.AddWithValue("lastId", lastBundleId.Value);
        }
        if (maxId.HasValue)
        {
            command.Parameters.AddWithValue("maxId", maxId.Value);
        }
        command.Parameters.AddWithValue("limit", limit);
        
        await using var reader = await command.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            var bundle = new Bundle
            {
                Id = reader.GetInt64(0),
                Type = reader.GetInt32(1),
                TenantId = reader.GetString(2),
                MemberId = reader.GetGuid(3),
                // Use mock data for sensitive fields
                Key = MockDataProvider.GetBundleKey(),
                Extensions = MockDataProvider.GetBundleExtension()
            };
            
            bundles.Add(bundle);
        }
        
        return bundles;
    }

    public async Task<Dictionary<Guid, List<Bundle>>> GetBundlesByMemberIdsAsync(List<Guid> memberIds)
    {
        var bundlesByMember = new Dictionary<Guid, List<Bundle>>();
        
        if (memberIds == null || !memberIds.Any())
        {
            return bundlesByMember;
        }
        
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        // Use temporary table approach for better performance with large batch lookups
        // This ensures PostgreSQL uses the ix_bundles_member_id index efficiently
        var tempTableQuery = @"
            CREATE TEMP TABLE temp_member_ids (member_id uuid) ON COMMIT DROP;
            INSERT INTO temp_member_ids (member_id) SELECT unnest(@memberIds);
            
            SELECT b.id, b.type, b.tenant_id, b.member_id
            FROM bundles b
            INNER JOIN temp_member_ids t ON b.member_id = t.member_id
            ORDER BY b.member_id, b.id";
        
        await using var command = new NpgsqlCommand(tempTableQuery, connection);
        command.Parameters.AddWithValue("memberIds", memberIds.ToArray());
        
        await using var reader = await command.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            var bundle = new Bundle
            {
                Id = reader.GetInt64(0),
                Type = reader.GetInt32(1),
                TenantId = reader.GetString(2),
                MemberId = reader.GetGuid(3),
                // Use mock data for sensitive fields
                Key = MockDataProvider.GetBundleKey(),
                Extensions = MockDataProvider.GetBundleExtension()
            };
            
            if (!bundlesByMember.ContainsKey(bundle.MemberId))
            {
                bundlesByMember[bundle.MemberId] = new List<Bundle>();
            }
            
            bundlesByMember[bundle.MemberId].Add(bundle);
        }
        
        return bundlesByMember;
    }

    public async Task<List<Guid>> GetMemberIdSamplesAsync(int numSamples, long interval)
    {
        var samples = new List<Guid>();
        
        if (numSamples <= 0)
        {
            return samples;
        }

        await using var connection = await _dataSource.OpenConnectionAsync();
        
        // Use a single query with UNION ALL for better performance
        var queryParts = new List<string>();
        for (int i = 1; i <= numSamples; i++)
        {
            queryParts.Add($"(SELECT id FROM members ORDER BY id LIMIT 1 OFFSET {i * interval})");
        }
        
        var query = string.Join(" UNION ALL ", queryParts);
        
        await using var command = new NpgsqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            samples.Add(reader.GetGuid(0));
        }
        
        return samples;
    }

    public async Task<List<long>> GetBundleIdSamplesAsync(int numSamples, long interval)
    {
        var samples = new List<long>();
        
        if (numSamples <= 0)
        {
            return samples;
        }

        await using var connection = await _dataSource.OpenConnectionAsync();
        
        // Use a single query with UNION ALL for better performance
        var queryParts = new List<string>();
        for (int i = 1; i <= numSamples; i++)
        {
            queryParts.Add($"(SELECT id FROM bundles ORDER BY id LIMIT 1 OFFSET {i * interval})");
        }
        
        var query = string.Join(" UNION ALL ", queryParts);
        
        await using var command = new NpgsqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            samples.Add(reader.GetInt64(0));
        }
        
        return samples;
    }
}
