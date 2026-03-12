using System.Collections.Generic;
using System.Text.Json;
using Deneblab.StashLock.Cli.Modules.Encode;

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

        if (parsed.ContainsKey("VaultKey"))
        {
            var wholeFile = JsonSerializer.Deserialize<StashlockEncryptedFile>(json, ReadOptions);
            return string.IsNullOrEmpty(wholeFile?.VaultKey) ? null : wholeFile.VaultKey;
        }

        return null;
    }
}
