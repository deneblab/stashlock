using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Deneblab.StashLock.Cli.Modules.Encode;

/// <summary>
/// Plaintext input of <c>encode</c>: <c>&lt;FILE&gt;.json</c> holds the shared values and an optional
/// <c>&lt;FILE&gt;.&lt;tag&gt;.json</c> next to it overrides them for one tag
/// (like <c>appsettings.json</c> + <c>appsettings.Production.json</c>).
/// </summary>
internal static class SecretsOverride
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string FileNameFor(string inputBaseName, string tag) => $"{inputBaseName}.{tag}.json";

    /// <summary>Reads a JSON object file as flat <c>A:B</c> keys; throws <see cref="JsonException"/> when it is not a JSON object.</summary>
    public static Dictionary<string, string> Read(string path)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path), ReadOptions)
                  ?? throw new JsonException("not a JSON object");
        var result = new Dictionary<string, string>();
        foreach (var (key, value) in raw)
            Flatten(value, key, result);
        return result;
    }

    /// <summary>
    /// Shared values with the override applied. An override key replaces the same key, and a value
    /// replaces a section (or a section a value) of the same name. Arrays are overridden per index.
    /// </summary>
    public static Dictionary<string, string> Merge(Dictionary<string, string> shared, Dictionary<string, string> overrides)
    {
        var result = new Dictionary<string, string>(shared);
        foreach (var (key, value) in overrides)
        {
            var replaced = result.Keys.Where(k => k.StartsWith(key + ":") || key.StartsWith(k + ":")).ToList();
            foreach (var k in replaced)
                result.Remove(k);
            result[key] = value;
        }
        return result;
    }

    private static void Flatten(JsonElement element, string key, Dictionary<string, string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    Flatten(prop.Value, $"{key}:{prop.Name}", result);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, $"{key}:{index}", result);
                    index++;
                }
                break;
            case JsonValueKind.String:
                result[key] = element.GetString()!;
                break;
            default:
                result[key] = element.GetRawText();
                break;
        }
    }
}
