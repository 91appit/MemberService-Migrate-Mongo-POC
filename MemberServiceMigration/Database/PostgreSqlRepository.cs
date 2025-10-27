using Npgsql;
using MemberServiceMigration.Models;
using System.Text.Json;

namespace MemberServiceMigration.Database;

public class PostgreSqlRepository
{
    private readonly string _connectionString;

    public PostgreSqlRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<List<Member>> GetAllMembersAsync()
    {
        var members = new List<Member>();
        
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        
        var query = @"
            SELECT id, password, salt, tenant_id, state, allow_login, extensions, 
                   create_at, create_user, update_at, update_user, version, tags, profile, tags_v2
            FROM members";
        
        await using var command = new NpgsqlCommand(query, connection);
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
                Extensions = reader.IsDBNull(6) ? null : JsonDocument.Parse(reader.GetString(6)),
                CreateAt = reader.GetDateTime(7),
                CreateUser = reader.IsDBNull(8) ? null : reader.GetString(8),
                UpdateAt = reader.GetDateTime(9),
                UpdateUser = reader.IsDBNull(10) ? null : reader.GetString(10),
                Version = reader.GetInt32(11),
                Tags = reader.IsDBNull(12) ? null : (string[])reader.GetValue(12),
                Profile = reader.IsDBNull(13) ? null : JsonDocument.Parse(reader.GetString(13)),
                TagsV2 = reader.IsDBNull(14) ? null : JsonDocument.Parse(reader.GetString(14))
            };
            
            members.Add(member);
        }
        
        return members;
    }

    public async Task<List<Bundle>> GetAllBundlesAsync()
    {
        var bundles = new List<Bundle>();
        
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        
        var query = @"
            SELECT id, key, type, tenant_id, extensions, member_id, 
                   create_at, create_user, update_at, update_user
            FROM bundles";
        
        await using var command = new NpgsqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            var bundle = new Bundle
            {
                Id = reader.GetInt64(0),
                Key = reader.GetString(1),
                Type = reader.GetInt32(2),
                TenantId = reader.GetString(3),
                Extensions = reader.IsDBNull(4) ? null : JsonDocument.Parse(reader.GetString(4)),
                MemberId = reader.GetGuid(5),
                CreateAt = reader.GetDateTime(6),
                CreateUser = reader.IsDBNull(7) ? null : reader.GetString(7),
                UpdateAt = reader.GetDateTime(8),
                UpdateUser = reader.IsDBNull(9) ? null : reader.GetString(9)
            };
            
            bundles.Add(bundle);
        }
        
        return bundles;
    }

    public async Task<Dictionary<Guid, List<Bundle>>> GetBundlesByMemberIdAsync()
    {
        var bundlesByMember = new Dictionary<Guid, List<Bundle>>();
        
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        
        var query = @"
            SELECT id, key, type, tenant_id, extensions, member_id, 
                   create_at, create_user, update_at, update_user
            FROM bundles
            ORDER BY member_id";
        
        await using var command = new NpgsqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            var bundle = new Bundle
            {
                Id = reader.GetInt64(0),
                Key = reader.GetString(1),
                Type = reader.GetInt32(2),
                TenantId = reader.GetString(3),
                Extensions = reader.IsDBNull(4) ? null : JsonDocument.Parse(reader.GetString(4)),
                MemberId = reader.GetGuid(5),
                CreateAt = reader.GetDateTime(6),
                CreateUser = reader.IsDBNull(7) ? null : reader.GetString(7),
                UpdateAt = reader.GetDateTime(8),
                UpdateUser = reader.IsDBNull(9) ? null : reader.GetString(9)
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
