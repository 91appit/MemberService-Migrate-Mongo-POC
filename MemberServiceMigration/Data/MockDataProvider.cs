using System.Text.Json;

namespace MemberServiceMigration.Data;

/// <summary>
/// Provides mock data for sensitive fields to improve query performance and meet security requirements.
/// </summary>
public static class MockDataProvider
{
    // Mock data for members.extension field
    private static readonly string _memberExtensionJson = @"{
        ""sourceCreatedAt"": ""2025-11-01T14:58:46.9000000Z"",
        ""sourceCreatedBy"": ""110070921"",
        ""sourceUpdatedAt"": ""2025-11-03T15:37:11.5600000Z"",
        ""sourceUpdatedBy"": ""csp_SyncCrmMemberTierDataToVipMember""
    }";

    // Mock data for members.profile field
    private static readonly string _memberProfileJson = @"{
        ""name"": {
            ""fullName"": ""林欣翰"",
            ""firstName"": ""林欣翰""
        },
        ""locale"": ""TW"",
        ""birthday"": {
            ""day"": 1,
            ""year"": 1999,
            ""month"": 10
        },
        ""carrierCode"": ""/TTTTTTT"",
        ""barcodeValue"": ""2770000000000"",
        ""contactEmail"": ""aaaaaaaa@gmail.com"",
        ""notifications"": [
            ""transaction_email"",
            ""tracking_email"",
            ""marketing_sms""
        ],
        ""membershipTier"": 90,
        ""outerMemberCode"": ""2770017067546"",
        ""eComJoinDateTime"": ""2022-05-20T10:47:41.6370000Z"",
        ""noticeLanguageType"": ""zh-TW"",
        ""migrateJoinDateTime"": ""2022-01-30T16:00:00.0000000Z"",
        ""onlineMembershipCard"": ""MembershipCard#2770000000000"",
        ""defaultMembershipCard"": ""MembershipCard#2770000000000"",
        ""onlineFirstPurchaseAt"": ""2023-12-14T01:50:28.0000000Z"",
        ""inStoreFirstPurchaseAt"": ""2022-06-04T10:06:00.0000000Z"",
        ""onlineFirstRepurchaseAt"": ""2024-03-30T05:17:23.0000000Z"",
        ""inStoreFirstRepurchaseAt"": ""2022-10-16T07:10:00.0000000Z"",
        ""onlineFirstPurchaseOrderNo"": ""TG231214KA00XQ"",
        ""inStoreFirstPurchaseOrderNo"": ""019220220604C00233"",
        ""onlineFirstRepurchaseOrderNo"": ""TG240330PA00K8"",
        ""inStoreFirstRepurchaseOrderNo"": ""025920221016B00211""
    }";

    // Mock data for bundles.extension field
    private static readonly string _bundleExtensionJson = @"{
        ""sourceCreatedAt"": ""2023-06-14T00:48:24.2500000Z"",
        ""sourceCreatedBy"": ""ShopEtlFlowId_413"",
        ""sourceUpdatedAt"": ""2023-09-14T04:45:08.7330000Z"",
        ""sourceUpdatedBy"": ""40909""
    }";

    // Lazy initialization for thread-safe singleton pattern
    private static readonly Lazy<JsonDocument> _memberExtension = new(() => JsonDocument.Parse(_memberExtensionJson));
    private static readonly Lazy<JsonDocument> _memberProfile = new(() => JsonDocument.Parse(_memberProfileJson));
    private static readonly Lazy<JsonDocument> _bundleExtension = new(() => JsonDocument.Parse(_bundleExtensionJson));

    /// <summary>
    /// Gets the mock extension data for members.
    /// </summary>
    public static JsonDocument GetMemberExtension() => _memberExtension.Value;

    /// <summary>
    /// Gets the mock tags array for members.
    /// </summary>
    public static string[] GetMemberTags() => new[]
    {
        "40916-MemberCollection-Tag-20240327121034",
        "40916-MemberCollection-Tag-20240425122710",
        "40916-MemberCollection-Tag-20240527164707",
        "40916-MemberCollection-Tag-20241129112310",
        "40916-MemberCollection-Tag-20241129110007",
        "NAPL::A+",
        "40916MemberCollection-Tag-20251003115836",
        "ProfileConflict::BarcodeValue",
        "ProfileConflict::MigrateJoinDateTime"
    };

    /// <summary>
    /// Gets the mock profile data for members.
    /// </summary>
    public static JsonDocument GetMemberProfile() => _memberProfile.Value;

    /// <summary>
    /// Gets the mock tags_v2 data for members (null according to spec).
    /// </summary>
    public static JsonDocument? GetMemberTagsV2() => null;

    /// <summary>
    /// Gets the mock extension data for bundles.
    /// </summary>
    public static JsonDocument GetBundleExtension() => _bundleExtension.Value;

    /// <summary>
    /// Gets the mock key value for bundles.
    /// </summary>
    public static string GetBundleKey() => "648359bb1a20cad74242c5b3";
}
