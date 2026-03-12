using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deneblab.StashLock.Client.Common;

public class SecretsFlattenedJsonConverter : JsonConverter<Dictionary<string, object>>
{
    private const string _DELIMITER = ":";

    public override Dictionary<string, object> Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        using (var doc = JsonDocument.ParseValue(ref reader))
        {
            var root = doc.RootElement;
            return FlattenJsonObject(root);
        }
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, object> value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }

    private Dictionary<string, object> FlattenJsonObject(JsonElement element, string prefix = "")
    {
        var result = new Dictionary<string, object>();

        foreach (var property in element.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                var nestedPrefix = prefix + property.Name + _DELIMITER;
                var nestedDict = FlattenJsonObject(property.Value, nestedPrefix);
                foreach (var nestedEntry in nestedDict) result.Add(nestedEntry.Key, nestedEntry.Value);
            }
            else
            {
                result.Add(prefix + property.Name, DeserializeJsonValue(property.Value));
            }

        return result;
    }

    private object DeserializeJsonValue(JsonElement jsonElement)
    {
        switch (jsonElement.ValueKind)
        {
            case JsonValueKind.Number:
                return jsonElement.GetInt32();
            case JsonValueKind.String:
                return jsonElement.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }
}