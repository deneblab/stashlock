using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Deneblab.StashLock.Client.Common;

/// <summary>
/// Reads a plain secrets JSON file into colon-separated keys ("Database:Password", "Hosts:0").
/// Accepts the documented flat/nested form and the older { "Values": { ... } } wrapper.
/// </summary>
internal static class SecretsJson
{
    public static Dictionary<string, string> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("The secrets file must contain a JSON object.");

        // Older format: everything under a single "Values" object
        var props = new List<JsonProperty>(root.EnumerateObject());
        if (props.Count == 1 && string.Equals(props[0].Name, "Values", StringComparison.OrdinalIgnoreCase)
            && props[0].Value.ValueKind == JsonValueKind.Object)
            root = props[0].Value;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Flatten(root, "", result);
        return result;
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    Flatten(prop.Value, prefix.Length == 0 ? prop.Name : $"{prefix}:{prop.Name}", result);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                    Flatten(item, $"{prefix}:{i++}", result);
                break;
            case JsonValueKind.Null:
                result[prefix] = string.Empty;
                break;
            default:
                result[prefix] = element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText();
                break;
        }
    }
}
