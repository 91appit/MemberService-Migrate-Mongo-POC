using MemberServiceMigration.Models;
using MemberServiceMigration.Models.MongoDB;
using MongoDB.Bson;
using System.Text.Json;

namespace MemberServiceMigration.Migration;

public static class DataConverter
{
    public static MemberDocument ConvertToMemberDocument(Member member)
    {
        return new MemberDocument
        {
            Id = member.Id,
            Password = member.Password,
            Salt = member.Salt,
            TenantId = member.TenantId,
            State = member.State,
            AllowLogin = member.AllowLogin,
            Extensions = ConvertJsonDocumentToBsonDocument(member.Extensions),
            CreateAt = member.CreateAt,
            CreateUser = member.CreateUser,
            UpdateAt = member.UpdateAt,
            UpdateUser = member.UpdateUser,
            Version = member.Version,
            Tags = member.Tags,
            Profile = ConvertJsonDocumentToBsonDocument(member.Profile),
            TagsV2 = ConvertJsonDocumentToBsonDocument(member.TagsV2)
        };
    }

    public static MemberDocumentEmbedding ConvertToMemberDocumentEmbedding(Member member, List<Bundle>? bundles)
    {
        var memberDoc = new MemberDocumentEmbedding
        {
            Id = member.Id,
            Password = member.Password,
            Salt = member.Salt,
            TenantId = member.TenantId,
            State = member.State,
            AllowLogin = member.AllowLogin,
            Extensions = ConvertJsonDocumentToBsonDocument(member.Extensions),
            CreateAt = member.CreateAt,
            CreateUser = member.CreateUser,
            UpdateAt = member.UpdateAt,
            UpdateUser = member.UpdateUser,
            Version = member.Version,
            Tags = member.Tags,
            Profile = ConvertJsonDocumentToBsonDocument(member.Profile),
            TagsV2 = ConvertJsonDocumentToBsonDocument(member.TagsV2)
        };

        if (bundles != null && bundles.Any())
        {
            memberDoc.Bundles = bundles.Select(ConvertToBundleEmbedded).ToList();
        }

        return memberDoc;
    }

    public static BundleEmbedded ConvertToBundleEmbedded(Bundle bundle)
    {
        return new BundleEmbedded
        {
            Id = bundle.Id,
            Key = bundle.Key,
            Type = bundle.Type,
            Extensions = ConvertJsonDocumentToBsonDocument(bundle.Extensions),
            CreateAt = bundle.CreateAt,
            CreateUser = bundle.CreateUser,
            UpdateAt = bundle.UpdateAt,
            UpdateUser = bundle.UpdateUser
        };
    }

    public static BundleDocument ConvertToBundleDocument(Bundle bundle)
    {
        return new BundleDocument
        {
            Id = bundle.Id,
            Key = bundle.Key,
            Type = bundle.Type,
            TenantId = bundle.TenantId,
            Extensions = ConvertJsonDocumentToBsonDocument(bundle.Extensions),
            MemberId = bundle.MemberId,
            CreateAt = bundle.CreateAt,
            CreateUser = bundle.CreateUser,
            UpdateAt = bundle.UpdateAt,
            UpdateUser = bundle.UpdateUser
        };
    }

    private static BsonDocument? ConvertJsonDocumentToBsonDocument(JsonDocument? jsonDocument)
    {
        if (jsonDocument == null)
            return null;

        var json = jsonDocument.RootElement.GetRawText();
        return BsonDocument.Parse(json);
    }
}
