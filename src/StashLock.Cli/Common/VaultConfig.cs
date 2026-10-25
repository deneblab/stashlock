using System.IO;
using System.Text.Json;
using Deneblab.StashLock.Cli.Models;

namespace Deneblab.StashLock.Cli.Common;

/// <summary>Reads <c>stashlock.config.json</c> (JSON with comments and trailing commas) from a vault directory.</summary>
internal static class VaultConfig
{
    public const string FileName = "stashlock.config.json";

    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string PathIn(string dir) => Path.Combine(dir, FileName);

    /// <summary>Returns null when the file does not exist; throws <see cref="JsonException"/> when it is not valid.</summary>
    public static StashlockConfig? Load(string dir)
    {
        var path = PathIn(dir);
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize<StashlockConfig>(File.ReadAllText(path), ReadOptions)
               ?? throw new JsonException($"{FileName} is empty.");
    }
}
