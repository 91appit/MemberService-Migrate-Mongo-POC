using System.Text.Json;

namespace MemberServiceMigration.Data;

/// <summary>
/// Provides data masking functions for sensitive fields to meet security requirements.
/// </summary>
public static class DataMaskingProvider
{
    /// <summary>
    /// Masks a phone number key for bundles with type 1001.
    /// Keeps the first 3 and last 3 digits, masks the middle part with asterisks.
    /// </summary>
    public static string MaskPhoneNumber(string phoneNumber)
    {
        if (string.IsNullOrEmpty(phoneNumber))
        {
            return phoneNumber;
        }

        // If phone number is too short, mask all but first and last character
        if (phoneNumber.Length <= 6)
        {
            if (phoneNumber.Length <= 2)
            {
                return new string('*', phoneNumber.Length);
            }
            return phoneNumber[0] + new string('*', phoneNumber.Length - 2) + phoneNumber[^1];
        }

        // For longer phone numbers, keep first 3 and last 3 digits
        return phoneNumber.Substring(0, 3) + new string('*', phoneNumber.Length - 6) + phoneNumber.Substring(phoneNumber.Length - 3);
    }

    /// <summary>
    /// Masks a bundle key based on the bundle type.
    /// For type 1001 (phone number), applies phone number masking.
    /// For other types, returns the key as-is.
    /// </summary>
    public static string MaskBundleKey(string key, int type)
    {
        if (type == 1001)
        {
            return MaskPhoneNumber(key);
        }
        return key;
    }

    /// <summary>
    /// Masks sensitive fields in the profile JSON.
    /// Masks: name.fullName, name.firstName, locale, birthday fields.
    /// </summary>
    public static JsonDocument? MaskProfile(JsonDocument? profile)
    {
        if (profile == null)
        {
            return null;
        }

        var root = profile.RootElement;
        
        // Create a dictionary to build the modified profile
        var profileDict = new Dictionary<string, object?>();

        // Copy all properties from the original profile
        foreach (var property in root.EnumerateObject())
        {
            profileDict[property.Name] = GetJsonValue(property.Value);
        }

        // Mask name fields if present
        if (root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.Object)
        {
            var nameDict = new Dictionary<string, object?>();
            foreach (var property in nameElement.EnumerateObject())
            {
                if (property.Name == "fullName" || property.Name == "firstName")
                {
                    nameDict[property.Name] = MaskName(property.Value.GetString() ?? string.Empty);
                }
                else
                {
                    nameDict[property.Name] = GetJsonValue(property.Value);
                }
            }
            profileDict["name"] = nameDict;
        }

        // Mask locale if present
        if (root.TryGetProperty("locale", out var localeElement))
        {
            profileDict["locale"] = MaskLocale(localeElement.GetString() ?? string.Empty);
        }

        // Mask birthday if present
        if (root.TryGetProperty("birthday", out var birthdayElement) && birthdayElement.ValueKind == JsonValueKind.Object)
        {
            var birthdayDict = new Dictionary<string, object?>();
            foreach (var property in birthdayElement.EnumerateObject())
            {
                if (property.Name == "day" || property.Name == "year" || property.Name == "month")
                {
                    birthdayDict[property.Name] = MaskBirthdayField();
                }
                else
                {
                    birthdayDict[property.Name] = GetJsonValue(property.Value);
                }
            }
            profileDict["birthday"] = birthdayDict;
        }

        // Convert the dictionary back to JsonDocument
        var json = JsonSerializer.Serialize(profileDict);
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Masks a person's name by keeping only the first character and replacing the rest with asterisks.
    /// </summary>
    private static string MaskName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        if (name.Length == 1)
        {
            return "*";
        }

        return name[0] + new string('*', name.Length - 1);
    }

    /// <summary>
    /// Masks locale information by replacing it with asterisks.
    /// </summary>
    private static string MaskLocale(string locale)
    {
        if (string.IsNullOrEmpty(locale))
        {
            return locale;
        }

        return new string('*', locale.Length);
    }

    /// <summary>
    /// Returns a masked value (0) for birthday fields.
    /// </summary>
    private static int MaskBirthdayField()
    {
        return 0;
    }

    /// <summary>
    /// Helper method to extract value from JsonElement.
    /// </summary>
    private static object? GetJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue) ? longValue : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => ConvertToDict(element),
            JsonValueKind.Array => ConvertToList(element),
            _ => null
        };
    }

    /// <summary>
    /// Converts JsonElement object to dictionary.
    /// </summary>
    private static Dictionary<string, object?> ConvertToDict(JsonElement element)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var property in element.EnumerateObject())
        {
            dict[property.Name] = GetJsonValue(property.Value);
        }
        return dict;
    }

    /// <summary>
    /// Converts JsonElement array to list.
    /// </summary>
    private static List<object?> ConvertToList(JsonElement element)
    {
        var list = new List<object?>();
        foreach (var item in element.EnumerateArray())
        {
            list.Add(GetJsonValue(item));
        }
        return list;
    }
}
