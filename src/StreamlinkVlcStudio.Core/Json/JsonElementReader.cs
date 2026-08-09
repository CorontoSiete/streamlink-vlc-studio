using System.Globalization;
using System.Text.Json;

namespace StreamlinkVlcStudio.Core.Json;

/// <summary>
/// Shared, null-safe readers for <see cref="JsonElement"/> values used across the Twitch
/// and Kick service integrations. These consolidate helper methods that were previously
/// copy-pasted into many services. All readers tolerate missing properties and non-object
/// elements by returning an empty/None result rather than throwing.
/// </summary>
public static class JsonElementReader
{
    /// <summary>
    /// Returns the trimmed string form of <paramref name="propertyName"/> on
    /// <paramref name="root"/>: strings are trimmed, numbers return their raw text, booleans
    /// return "true"/"false", and anything else (including a missing property or a non-object
    /// element) returns an empty string.
    /// </summary>
    public static string GetOptionalString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var property))
        {
            return "";
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? "",
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    /// <summary>
    /// Reads <paramref name="propertyName"/> as a non-empty string without trimming: returns
    /// false for missing properties, non-object elements, non-string values, and empty strings.
    /// </summary>
    public static bool TryGetNonEmptyString(JsonElement element, string propertyName, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return value.Length > 0;
        }

        value = "";
        return false;
    }

    public static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    /// <summary>Reads <paramref name="element"/> itself (already the value) as an Int64.</summary>
    public static long? TryGetInt64(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    /// <summary>Reads <paramref name="propertyName"/> on <paramref name="element"/> as an Int64.</summary>
    public static long? TryGetInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    public static bool? TryGetBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    public static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        var value = GetOptionalString(element, propertyName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Reads a string from a nested object: <paramref name="objectPropertyName"/> on
    /// <paramref name="element"/>, then <paramref name="nestedPropertyName"/> on that object.
    /// </summary>
    public static string TryReadNestedString(JsonElement element, string objectPropertyName, string nestedPropertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(objectPropertyName, out var nested) &&
            nested.ValueKind == JsonValueKind.Object
            ? GetOptionalString(nested, nestedPropertyName)
            : "";
    }

    /// <summary>
    /// Returns the first non-empty <paramref name="propertyName"/> string found among the object
    /// items of the array property <paramref name="arrayPropertyName"/> on <paramref name="element"/>.
    /// </summary>
    public static string TryReadFirstArrayObjectString(JsonElement element, string arrayPropertyName, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(arrayPropertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var value = GetOptionalString(item, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return "";
    }

    /// <summary>Reads the cursor (default property "cursor") from a "pagination" object.</summary>
    public static string ReadPaginationCursor(JsonElement root, string propertyName = "cursor")
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("pagination", out var pagination) ||
            pagination.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        return GetOptionalString(pagination, propertyName);
    }
}
