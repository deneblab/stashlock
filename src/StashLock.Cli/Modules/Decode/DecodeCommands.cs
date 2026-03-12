using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ConsoleAppFramework;
using Deneblab.StashLock.Cli.Modules.Ecies;
using Deneblab.StashLock.Cli.Modules.Encode;
using Microsoft.Extensions.Logging;

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
    ///   stashlock decode ./my-vault/stashlock.enc.production.secrets.json -key ./keys/stashlock.key.production.json
    ///   stashlock decode ./my-vault/stashlock.enc.production.secrets.json -output ./decrypted.json
    /// </summary>
    /// <param name="file">Path to the encrypted stashlock.enc.*.json file</param>
    /// <param name="key">-key, Optional path to the key file (auto-detected from tag if omitted)</param>
    /// <param name="output">-output, Optional output file path (prints to stdout if omitted)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [Command("")]
    public Task Root(
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
            Console.WriteLine($"Error: Encrypted file not found: {filePath}");
            Console.ResetColor();
            return Task.CompletedTask;
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
                Console.WriteLine("Error: Failed to parse encrypted file");
                Console.ResetColor();
                return Task.CompletedTask;
            }
        }
        catch (JsonException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Invalid JSON in encrypted file: {ex.Message}");
            Console.ResetColor();
            return Task.CompletedTask;
        }

        // Auto-detect mode
        string result;
        if (parsed.ContainsKey("_stashlock"))
        {
            result = DecodeSopsMode(json, key, fileDir);
        }
        else if (parsed.ContainsKey("EncryptedData"))
        {
            result = DecodeWholeFileMode(json, key, fileDir);
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: Unrecognized encrypted file format.");
            Console.WriteLine("Expected '_stashlock' (sops mode) or 'EncryptedData' (wholefile mode).");
            Console.ResetColor();
            return Task.CompletedTask;
        }

        if (result == null!)
            return Task.CompletedTask;

        if (!string.IsNullOrEmpty(output))
        {
            var outputPath = Path.IsPathFullyQualified(output)
                ? output
                : Path.GetFullPath(output);
            File.WriteAllText(outputPath, result);
            _log.LogInformation("Decrypted output written to {Path}", outputPath);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Decrypted: {outputPath}");
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine(result);
        }

        return Task.CompletedTask;
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
        _log.LogInformation("Decoding SOPS file, vaultKey: {VaultKey}", vaultKey);

        // Load private key
        var keyPair = LoadKeyFile(vaultKey, keyOverride, fileDir);
        if (keyPair == null)
            return null!;

        // Unwrap data key
        byte[] dataKey;
        try
        {
            var wrappedDataKey = Convert.FromBase64String(sopsFile.Metadata.DataKey);
            dataKey = EciesModule.Open(wrappedDataKey, keyPair.GetPrivateKeyBytes());
        }
        catch (Exception ex)
        {
            PrintError($"Error: Failed to unwrap data key — {ex.Message}");
            _log.LogError(ex, "Failed to unwrap data key");
            return null!;
        }

        // Verify MAC
        try
        {
            var macBase64 = AesGcmHelper.DecryptValue(dataKey, sopsFile.Metadata.Mac);
            var expectedMac = Convert.FromBase64String(macBase64);
            var encryptedValues = new SortedDictionary<string, string>(sopsFile.Values, StringComparer.Ordinal);

            if (!AesGcmHelper.VerifyMac(dataKey, encryptedValues, expectedMac))
            {
                PrintError("Error: MAC verification failed — file may have been tampered with");
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(dataKey);
                return null!;
            }
        }
        catch (Exception ex)
        {
            PrintError($"Error: MAC verification failed — {ex.Message}");
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(dataKey);
            return null!;
        }

        // Decrypt all values
        var decrypted = new Dictionary<string, string>();
        try
        {
            foreach (var kvp in sopsFile.Values)
            {
                decrypted[kvp.Key] = AesGcmHelper.DecryptValue(dataKey, kvp.Value);
            }
        }
        catch (Exception ex)
        {
            PrintError($"Error: Failed to decrypt value — {ex.Message}");
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(dataKey);
            return null!;
        }

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(dataKey);

        // Unflatten colon-separated keys back to nested JSON
        var nested = UnflattenToJsonElement(decrypted);

        _log.LogInformation("Successfully decoded {Count} values (sops mode)", decrypted.Count);
        return JsonSerializer.Serialize(nested, WriteOptions);
    }

    private string DecodeWholeFileMode(string json, string keyOverride, string fileDir)
    {
        var encFile = JsonSerializer.Deserialize<StashlockEncryptedFile>(json, ReadOptions);
        if (encFile == null || string.IsNullOrEmpty(encFile.EncryptedData))
        {
            PrintError("Error: Failed to parse whole-file encrypted structure");
            return null!;
        }

        var vaultKey = encFile.VaultKey;
        _log.LogInformation("Decoding whole-file, vaultKey: {VaultKey}", vaultKey);

        var keyPair = LoadKeyFile(vaultKey, keyOverride, fileDir);
        if (keyPair == null)
            return null!;

        try
        {
            var sealedBytes = Convert.FromBase64String(encFile.EncryptedData);
            var plaintext = EciesModule.Open(sealedBytes, keyPair.GetPrivateKeyBytes());
            var plaintextJson = Encoding.UTF8.GetString(plaintext);

            // Pretty-print if valid JSON
            try
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(plaintextJson, ReadOptions);
                plaintextJson = JsonSerializer.Serialize(doc, WriteOptions);
            }
            catch
            {
                // Return as-is if not valid JSON
            }

            _log.LogInformation("Successfully decoded whole-file");
            return plaintextJson;
        }
        catch (Exception ex)
        {
            PrintError($"Error: Decryption failed — {ex.Message}");
            _log.LogError(ex, "Whole-file decryption failed");
            return null!;
        }
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
            Console.WriteLine("Hint: Use -key to specify the key file path, e.g.:");
            Console.WriteLine("  stashlock decode <file> -key path/to/stashlock.key.<tag>.json");
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
        Console.WriteLine(message);
        Console.ResetColor();
    }
}
