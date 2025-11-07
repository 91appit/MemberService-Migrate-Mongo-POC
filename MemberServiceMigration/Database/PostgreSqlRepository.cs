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

    public async Task<long> GetBundlesCountAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var query = "SELECT COUNT(*) FROM bundles";
        await using var command = new NpgsqlCommand(query, connection);
        
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
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
                       create_at, create_user, update_at, update_user, version,
                       extensions, tags, profile, tags_v2
                FROM members
                WHERE id > @lastId
                ORDER BY id
                LIMIT @limit";
        }
        else
        {
            query = @"
                SELECT id, password, salt, tenant_id, state, allow_login, 
                       create_at, create_user, update_at, update_user, version,
                       extensions, tags, profile, tags_v2
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
            // Read profile from database
            JsonDocument? profile = null;
            if (!reader.IsDBNull(13))
            {
                var profileJson = reader.GetString(13);
                profile = JsonDocument.Parse(profileJson);
            }

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
                // Read extensions from database or use mock data
                Extensions = reader.IsDBNull(11) ? MockDataProvider.GetMemberExtension() : JsonDocument.Parse(reader.GetString(11)),
                // Read tags from database or use mock data
                Tags = reader.IsDBNull(12) ? MockDataProvider.GetMemberTags() : reader.GetFieldValue<string[]>(12),
                // Mask sensitive profile fields
                Profile = DataMaskingProvider.MaskProfile(profile),
                // Read tags_v2 from database
                TagsV2 = reader.IsDBNull(14) ? null : JsonDocument.Parse(reader.GetString(14))
            };
            
            members.Add(member);
        }
        
        return members;
    }

    public async Task<List<Member>> GetMembersBatchByUpdateAtRangeAsync(
        DateTime? startDate,
        DateTime? endDate,
        Guid? lastMemberId,
        int limit)
    {
        var members = new List<Member>();
        
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var whereConditions = new List<string>();
        
        if (startDate.HasValue)
        {
            whereConditions.Add("update_at > @startDate");
        }
        
        if (endDate.HasValue)
        {
            whereConditions.Add("update_at <= @endDate");
        }
        
        if (lastMemberId.HasValue)
        {
            whereConditions.Add("id > @lastId");
        }
        
        var whereClause = whereConditions.Any() 
            ? "WHERE " + string.Join(" AND ", whereConditions)
            : "";
        
        var query = $@"
            SELECT id, password, salt, tenant_id, state, allow_login, 
                   create_at, create_user, update_at, update_user, version,
                   extensions, tags, profile, tags_v2
            FROM members
            {whereClause}
            ORDER BY id
            LIMIT @limit";
        
        await using var command = new NpgsqlCommand(query, connection);
        
        if (startDate.HasValue)
        {
            command.Parameters.AddWithValue("startDate", startDate.Value);
        }
        
        if (endDate.HasValue)
        {
            command.Parameters.AddWithValue("endDate", endDate.Value);
        }
        
        if (lastMemberId.HasValue)
        {
            command.Parameters.AddWithValue("lastId", lastMemberId.Value);
        }
        
        command.Parameters.AddWithValue("limit", limit);
        
        await using var reader = await command.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            // Read profile from database
            JsonDocument? profile = null;
            if (!reader.IsDBNull(13))
            {
                var profileJson = reader.GetString(13);
                profile = JsonDocument.Parse(profileJson);
            }

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
                // Read extensions from database or use mock data
                Extensions = reader.IsDBNull(11) ? MockDataProvider.GetMemberExtension() : JsonDocument.Parse(reader.GetString(11)),
                // Read tags from database or use mock data
                Tags = reader.IsDBNull(12) ? MockDataProvider.GetMemberTags() : reader.GetFieldValue<string[]>(12),
                // Mask sensitive profile fields
                Profile = DataMaskingProvider.MaskProfile(profile),
                // Read tags_v2 from database
                TagsV2 = reader.IsDBNull(14) ? null : JsonDocument.Parse(reader.GetString(14))
            };
            
            members.Add(member);
        }
        
        return members;
    }

    public async Task<long> GetMembersCountByUpdateAtRangeAsync(DateTime? startDate, DateTime? endDate)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var whereConditions = new List<string>();
        
        if (startDate.HasValue)
        {
            whereConditions.Add("update_at > @startDate");
        }
        
        if (endDate.HasValue)
        {
            whereConditions.Add("update_at <= @endDate");
        }
        
        var whereClause = whereConditions.Any() 
            ? "WHERE " + string.Join(" AND ", whereConditions)
            : "";
        
        var query = $"SELECT COUNT(*) FROM members {whereClause}";
        
        await using var command = new NpgsqlCommand(query, connection);
        
        if (startDate.HasValue)
        {
            command.Parameters.AddWithValue("startDate", startDate.Value);
        }
        
        if (endDate.HasValue)
        {
            command.Parameters.AddWithValue("endDate", endDate.Value);
        }
        
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    public async Task<List<Bundle>> GetBundlesBatchAsync(long? lastBundleId, int limit)
    {
        var bundles = new List<Bundle>();
        
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        string query;
        if (lastBundleId.HasValue)
        {
            query = @"
                SELECT id, key, type, tenant_id, member_id, extensions
                FROM bundles
                WHERE id > @lastId
                ORDER BY id
                LIMIT @limit";
        }
        else
        {
            query = @"
                SELECT id, key, type, tenant_id, member_id, extensions
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
            var bundleId = reader.GetInt64(0);
            var key = reader.GetString(1);
            var type = reader.GetInt32(2);
            var tenantId = reader.GetString(3);
            var memberId = reader.GetGuid(4);
            
            // Read extensions from database or use mock data
            JsonDocument? extensions = null;
            if (!reader.IsDBNull(5))
            {
                var extensionsJson = reader.GetString(5);
                extensions = JsonDocument.Parse(extensionsJson);
            }
            else
            {
                extensions = MockDataProvider.GetBundleExtension();
            }

            var bundle = new Bundle
            {
                Id = bundleId,
                Type = type,
                TenantId = tenantId,
                MemberId = memberId,
                // Mask key based on bundle type
                Key = DataMaskingProvider.MaskBundleKey(key, type),
                Extensions = extensions
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
            
            SELECT b.id, b.key, b.type, b.tenant_id, b.member_id, b.extensions
            FROM bundles b
            INNER JOIN temp_member_ids t ON b.member_id = t.member_id
            ORDER BY b.member_id, b.id";
        
        await using var command = new NpgsqlCommand(tempTableQuery, connection);
        command.Parameters.AddWithValue("memberIds", memberIds.ToArray());
        
        await using var reader = await command.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            var bundleId = reader.GetInt64(0);
            var key = reader.GetString(1);
            var type = reader.GetInt32(2);
            var tenantId = reader.GetString(3);
            var memberId = reader.GetGuid(4);
            
            // Read extensions from database or use mock data
            JsonDocument? extensions = null;
            if (!reader.IsDBNull(5))
            {
                var extensionsJson = reader.GetString(5);
                extensions = JsonDocument.Parse(extensionsJson);
            }
            else
            {
                extensions = MockDataProvider.GetBundleExtension();
            }

            var bundle = new Bundle
            {
                Id = bundleId,
                Type = type,
                TenantId = tenantId,
                MemberId = memberId,
                // Mask key based on bundle type
                Key = DataMaskingProvider.MaskBundleKey(key, type),
                Extensions = extensions
            };
            
            if (!bundlesByMember.ContainsKey(bundle.MemberId))
            {
                bundlesByMember[bundle.MemberId] = new List<Bundle>();
            }
            
            bundlesByMember[bundle.MemberId].Add(bundle);
        }
        
        return bundlesByMember;
    }
}
