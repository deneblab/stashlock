using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ConsoleAppFramework;
using Deneblab.StashLock.Cli.Common;
using Deneblab.StashLock.Cli.Common.Security;
using Deneblab.StashLock.Cli.Modules.Ecies;
using Deneblab.StashLock.Cli.Modules.Encode;
using Microsoft.Extensions.Logging;
using Deneblab.StashLock.Shared.Crypto;
using Deneblab.StashLock.Shared.Formats;

namespace Deneblab.StashLock.Cli.Modules.Decode;

internal class DecodeCommands
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ILogger<DecodeCommands> _log;

    public DecodeCommands(ILogger<DecodeCommands> log)
    {
        _log = log;
    }

    /// <summary>
    /// Decrypt an encrypted stashlock file and output the plaintext secrets.
    ///
    /// Examples:
    ///   stashlock decode ./my-vault/stashlock.enc.production.secrets.json
    ///   stashlock decode ./my-vault/stashlock.enc.production.secrets.json --key ./keys/stashlock.key.production.json
    ///   stashlock decode ./my-vault/stashlock.enc.production.secrets.json --output ./decrypted.json
    /// </summary>
    /// <param name="file">Path to the encrypted stashlock.enc.*.json file</param>
    /// <param name="key">-key, Optional path to the key file (auto-detected from tag if omitted)</param>
    /// <param name="output">-output, Optional output file path (prints to stdout if omitted)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [Command("")]
    public Task<int> Root(
        [Argument] string file,
        string key = "",
        string output = "",
        CancellationToken cancellationToken = default)
    {
        var filePath = Path.IsPathFullyQualified(file)
            ? file
            : Path.GetFullPath(file);

        if (!File.Exists(filePath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: Encrypted file not found: {filePath}");
            Console.ResetColor();
            return Task.FromResult(ExitCodes.Error);
        }

        var fileDir = Path.GetDirectoryName(filePath)!;
        var json = File.ReadAllText(filePath);

        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, ReadOptions);
            if (parsed == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine("Error: Failed to parse encrypted file");
                Console.ResetColor();
                return Task.FromResult(ExitCodes.Error);
            }
        }
        catch (JsonException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: Invalid JSON in encrypted file: {ex.Message}");
            Console.ResetColor();
            return Task.FromResult(ExitCodes.Error);
        }

        if (!parsed.ContainsKey("_stashlock"))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("Error: Not a StashLock encrypted file (no '_stashlock' metadata).");
            Console.Error.WriteLine("Files from older CLI versions are not supported; encode the secrets again.");
            Console.ResetColor();
            return Task.FromResult(ExitCodes.Error);
        }
        var result = DecodeSopsMode(json, key, fileDir);

        if (result == null!)
            return Task.FromResult(ExitCodes.Error);

        if (!string.IsNullOrEmpty(output))
        {
            var outputPath = Path.IsPathFullyQualified(output)
                ? output
                : Path.GetFullPath(output);
            SecureFile.WriteAllText(outputPath, result);
            _log.LogInformation("Decrypted output written to {Path}", outputPath);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Decrypted: {outputPath}");
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine(result);
        }

        return Task.FromResult(ExitCodes.Ok);
    }

    private string DecodeSopsMode(string json, string keyOverride, string fileDir)
    {
        var sopsFile = JsonSerializer.Deserialize<SopsEncryptedFile>(json, ReadOptions);
        if (sopsFile?.Metadata == null || sopsFile.Values == null)
        {
            PrintError("Error: Failed to parse SOPS encrypted file structure");
            return null!;
        }

        var vaultKey = sopsFile.Metadata.VaultKey;
        _log.LogInformation("Decoding SOPS file ({Mode}), vaultKey: {VaultKey}", sopsFile.Metadata.Mode, vaultKey);

        // Load private key
        var keyPair = LoadKeyFile(vaultKey, keyOverride, fileDir);
        if (keyPair == null)
            return null!;

        Dictionary<string, string> decrypted;
        try
        {
            decrypted = SopsCodec.Decrypt(sopsFile, keyPair.GetPrivateKeyBytes());
        }
        catch (Exception ex)
        {
            PrintError($"Error: {ex.Message}");
            _log.LogError(ex, "SOPS decryption failed");
            return null!;
        }

        // Unflatten colon-separated keys back to nested JSON
        var nested = UnflattenToJsonElement(decrypted);

        _log.LogInformation("Successfully decoded {Count} values ({Mode})", decrypted.Count, sopsFile.Metadata.Mode);
        return JsonSerializer.Serialize(nested, WriteOptions);
    }

    private KeyPairModel? LoadKeyFile(string vaultKey, string keyOverride, string fileDir)
    {
        string keyFilePath;

        if (!string.IsNullOrEmpty(keyOverride))
        {
            keyFilePath = Path.IsPathFullyQualified(keyOverride)
                ? keyOverride
                : Path.GetFullPath(keyOverride);
        }
        else
        {
            // Parse tag from vaultKey: "name.tag.version"
            var tag = ParseTagFromVaultKey(vaultKey);
            if (tag == null)
            {
                PrintError($"Error: Cannot parse tag from vault key: {vaultKey}");
                return null;
            }

            keyFilePath = Path.Combine(fileDir, $"stashlock.key.{tag}.json");
        }

        if (!File.Exists(keyFilePath))
        {
            PrintError($"Error: Key file not found: {keyFilePath}");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Error.WriteLine("Hint: Use --key to specify the key file path, e.g.:");
            Console.Error.WriteLine("  stashlock decode <file> --key path/to/stashlock.key.<tag>.json");
            Console.ResetColor();
            return null;
        }

        try
        {
            var keyJson = File.ReadAllText(keyFilePath);
            var keyPair = JsonSerializer.Deserialize<KeyPairModel>(keyJson, ReadOptions);
            if (keyPair == null)
            {
                PrintError($"Error: Failed to parse key file: {keyFilePath}");
                return null;
            }

            _log.LogInformation("Loaded key file: {Path}", keyFilePath);
            return keyPair;
        }
        catch (Exception ex)
        {
            PrintError($"Error: Failed to read key file: {ex.Message}");
            return null;
        }
    }

    private static string? ParseTagFromVaultKey(string vaultKey)
    {
        if (string.IsNullOrEmpty(vaultKey))
            return null;

        // vaultKey format: "name.tag.version"
        var parts = vaultKey.Split('.');
        if (parts.Length < 3)
            return null;

        // Tag is the second-to-last part
        return parts[^2];
    }

    private static JsonElement UnflattenToJsonElement(Dictionary<string, string> flat)
    {
        using var doc = JsonDocument.Parse("{}");
        var root = new Dictionary<string, object>();

        foreach (var kvp in flat)
        {
            var segments = kvp.Key.Split(':');
            SetNestedValue(root, segments, 0, kvp.Value);
        }

        var json = JsonSerializer.Serialize(root, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static void SetNestedValue(Dictionary<string, object> current, string[] segments, int index, string value)
    {
        var segment = segments[index];

        if (index == segments.Length - 1)
        {
            // Leaf — try to preserve original JSON types
            current[segment] = ParseLeafValue(value);
            return;
        }

        var nextSegment = segments[index + 1];
        var nextIsArrayIndex = int.TryParse(nextSegment, out _);

        if (!current.TryGetValue(segment, out var existing))
        {
            if (nextIsArrayIndex)
            {
                var list = new List<object>();
                current[segment] = list;
                SetNestedValueInList(list, segments, index + 1, value);
            }
            else
            {
                var dict = new Dictionary<string, object>();
                current[segment] = dict;
                SetNestedValue(dict, segments, index + 1, value);
            }
        }
        else if (existing is Dictionary<string, object> existingDict)
        {
            SetNestedValue(existingDict, segments, index + 1, value);
        }
        else if (existing is List<object> existingList)
        {
            SetNestedValueInList(existingList, segments, index + 1, value);
        }
    }

    private static void SetNestedValueInList(List<object> list, string[] segments, int index, string value)
    {
        if (!int.TryParse(segments[index], out var arrayIndex))
            return;

        // Extend list if needed
        while (list.Count <= arrayIndex)
            list.Add(null!);

        if (index == segments.Length - 1)
        {
            list[arrayIndex] = ParseLeafValue(value);
            return;
        }

        var nextSegment = segments[index + 1];
        var nextIsArrayIndex = int.TryParse(nextSegment, out _);

        if (list[arrayIndex] == null!)
        {
            if (nextIsArrayIndex)
            {
                var innerList = new List<object>();
                list[arrayIndex] = innerList;
                SetNestedValueInList(innerList, segments, index + 1, value);
            }
            else
            {
                var dict = new Dictionary<string, object>();
                list[arrayIndex] = dict;
                SetNestedValue(dict, segments, index + 1, value);
            }
        }
        else if (list[arrayIndex] is Dictionary<string, object> existingDict)
        {
            SetNestedValue(existingDict, segments, index + 1, value);
        }
        else if (list[arrayIndex] is List<object> existingList)
        {
            SetNestedValueInList(existingList, segments, index + 1, value);
        }
    }

    private static object ParseLeafValue(string value)
    {
        // Try to restore original JSON types
        if (value == "true") return true;
        if (value == "false") return false;
        if (value == "null") return null!;
        if (long.TryParse(value, out var longVal)) return longVal;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var dblVal)
            && value.Contains('.'))
            return dblVal;
        return value;
    }

    private static void PrintError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(message);
        Console.ResetColor();
    }
}
