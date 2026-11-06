using System.Collections.Generic;
using System.Text.Json;
using Deneblab.StashLock.Shared.Formats;

namespace Deneblab.StashLock.Cli.Modules.Publish;

internal static class VaultKeyExtractor
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string? Extract(string json)
    {
        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, ReadOptions);
            if (parsed == null)
                return null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed.ContainsKey("_stashlock"))
        {
            var sopsFile = JsonSerializer.Deserialize<SopsEncryptedFile>(json, ReadOptions);
            return sopsFile?.Metadata?.VaultKey;
        }

        return null;
    }

    /// <summary>Tag of a vault key <c>name.tag.version</c> (second-to-last part); null when the format does not match.</summary>
    public static string? TagOf(string? vaultKey)
    {
        if (string.IsNullOrEmpty(vaultKey))
            return null;
        var parts = vaultKey.Split('.');
        return parts.Length < 3 ? null : parts[^2];
    }
}
