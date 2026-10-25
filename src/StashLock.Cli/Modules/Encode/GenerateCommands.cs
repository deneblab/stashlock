using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ConsoleAppFramework;
using Deneblab.StashLock.Cli.Common;
using Deneblab.StashLock.Cli.Models;
using Deneblab.StashLock.Cli.Modules.Ecies;
using Microsoft.Extensions.Logging;
using Deneblab.StashLock.Shared.Crypto;
using Deneblab.StashLock.Shared.Formats;

namespace Deneblab.StashLock.Cli.Modules.Encode;

internal class GenerateCommands
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly ILogger<GenerateCommands> _log;

    public GenerateCommands(ILogger<GenerateCommands> log)
    {
        _log = log;
    }

    /// <summary>
    /// Encrypt a plaintext JSON secrets file using keys from the vault directory.
    ///
    /// Examples:
    ///   stashlock encode ./my-vault/secrets.json
    ///   stashlock encode ./my-vault/secrets.json --verbose
    ///   stashlock encode ./my-vault/secrets.json --format 1   # for clients older than SOPS v2
    /// </summary>
    /// <param name="file">Path to the plaintext JSON secrets file</param>
    /// <param name="verbose">Show resolved configuration before encoding</param>
    /// <param name="format">SOPS file format: 2 (default) or 1 for clients that cannot read v2 yet</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [Command("")]
    public Task<int> Root(
        [Argument] string file,
        bool verbose = false,
        int format = 2,
        CancellationToken cancellationToken = default)
    {
        if (format != 1 && format != 2)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: --format must be 1 or 2 (got {format})");
            Console.ResetColor();
            return Task.FromResult(ExitCodes.Error);
        }

        var secretFilePath = Path.IsPathFullyQualified(file)
            ? file
            : Path.GetFullPath(file);

        if (!File.Exists(secretFilePath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: Secrets file not found: {secretFilePath}");
            Console.ResetColor();
            return Task.FromResult(ExitCodes.Error);
        }

        var secretsJson = File.ReadAllText(secretFilePath);
        Dictionary<string, JsonElement>? secretsRaw;
        try
        {
            secretsRaw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(secretsJson, ReadOptions);
            if (secretsRaw == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine("Error: Failed to parse secrets file as JSON object");
                Console.ResetColor();
                return Task.FromResult(ExitCodes.Error);
            }
        }
        catch (JsonException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: Invalid JSON in secrets file: {ex.Message}");
            Console.ResetColor();
            return Task.FromResult(ExitCodes.Error);
        }

        var secretsDict = new Dictionary<string, string>();
        FlattenJson(secretsRaw, "", secretsDict);

        var secretsDir = Path.GetDirectoryName(secretFilePath)!;
        var configPath = Path.Combine(secretsDir, "stashlock.config.json");
        if (!File.Exists(configPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: stashlock.config.json not found at {configPath}");
            Console.Error.WriteLine("Run 'stashlock init' first to create the configuration.");
            Console.ResetColor();
            return Task.FromResult(ExitCodes.Error);
        }

        var configJson = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<StashlockConfig>(configJson, ReadOptions);
        if (config == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("Error: Failed to parse stashlock.config.json");
            Console.ResetColor();
            return Task.FromResult(ExitCodes.Error);
        }

        if (string.IsNullOrWhiteSpace(config.Name))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("Error: 'Name' is required in stashlock.config.json but is empty.");
            Console.Error.WriteLine("Run 'stashlock init' again or set the \"Name\" field manually.");
            Console.ResetColor();
            return Task.FromResult(ExitCodes.Error);
        }

        var keyFiles = FindKeyFiles(secretsDir);
        if (keyFiles.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: No key files (stashlock.key.*.json) found in {secretsDir}");
            Console.Error.WriteLine("Run 'stashlock keygen <vault dir>' to generate key files.");
            Console.ResetColor();
            return Task.FromResult(ExitCodes.Error);
        }

        if (verbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Error.WriteLine($"  Config:     {configPath}");
            Console.Error.WriteLine($"  Vault:      {config.Name}");
            Console.Error.WriteLine($"  Version:    {config.Version}");
            Console.Error.WriteLine($"  Mode:       {config.EncryptionMode ?? "sops"}");
            Console.Error.WriteLine($"  Key files:  {keyFiles.Count} ({string.Join(", ", keyFiles.Keys)})");
            Console.ResetColor();
            Console.WriteLine();
        }

        var mode = config.EncryptionMode ?? "sops";
        var modeLabel = mode.Equals("sops", StringComparison.OrdinalIgnoreCase) ? (format == 2 ? SopsCodec.ModeV2 : SopsCodec.ModeV1) : mode;
        var inputBaseName = Path.GetFileNameWithoutExtension(secretFilePath);
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var generated = 0;

        foreach (var (tag, keyPair) in keyFiles)
        {
            var vaultKey = $"{config.Name}.{tag}.{config.Version}";
            string outputFileName;
            string outputJson;

            if (mode.Equals("sops", StringComparison.OrdinalIgnoreCase))
            {
                outputJson = GenerateSopsMode(secretsDict, keyPair, vaultKey, jsonOptions, format);
                outputFileName = $"stashlock.enc.{tag}.{inputBaseName}.json";
            }
            else
            {
                outputJson = GenerateWholeFileMode(secretsJson, keyPair, vaultKey, jsonOptions);
                outputFileName = $"stashlock.enc.{tag}.{inputBaseName}.json";
            }

            var outputPath = Path.Combine(secretsDir, outputFileName);
            File.WriteAllText(outputPath, outputJson);

            _log.LogInformation("Generated encrypted file for tag {Tag}: {File} (mode: {Mode})", tag, outputPath, mode);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Generated: {outputFileName}  (vault key: {vaultKey}, mode: {modeLabel})");
            Console.ResetColor();
            generated++;
        }

        Console.WriteLine();
        Console.WriteLine($"Encode complete: {generated} encrypted file(s) created (mode: {modeLabel}).");
        return Task.FromResult(ExitCodes.Ok);
    }

    private static string GenerateSopsMode(
        Dictionary<string, string> secrets,
        KeyPairModel keyPair,
        string vaultKey,
        JsonSerializerOptions jsonOptions,
        int format)
    {
        var output = SopsCodec.Encrypt(secrets, keyPair.GetPublicKeyBytes(), vaultKey, format);
        return JsonSerializer.Serialize(output, jsonOptions);
    }

    private static string GenerateWholeFileMode(
        string secretsJson,
        KeyPairModel keyPair,
        string vaultKey,
        JsonSerializerOptions jsonOptions)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(secretsJson);
        var sealedBytes = EciesModule.Seal(plaintextBytes, keyPair.GetPublicKeyBytes());

        var output = new StashlockEncryptedFile
        {
            VaultKey = vaultKey,
            EncryptedData = Convert.ToBase64String(sealedBytes)
        };

        return JsonSerializer.Serialize(output, jsonOptions);
    }

    private static Dictionary<string, KeyPairModel> FindKeyFiles(string dir)
    {
        var result = new Dictionary<string, KeyPairModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in Directory.GetFiles(dir, "stashlock.key.*.json"))
        {
            var fileName = Path.GetFileName(filePath);
            var parts = fileName.Split('.');
            if (parts.Length < 4) continue;
            if (!parts[1].Equals("key", StringComparison.OrdinalIgnoreCase)) continue;

            var tagPart = fileName.Substring("stashlock.key.".Length,
                fileName.Length - "stashlock.key.".Length - ".json".Length);
            if (string.IsNullOrEmpty(tagPart)) continue;

            try
            {
                var json = File.ReadAllText(filePath);
                var keyPairData = JsonSerializer.Deserialize<KeyPairModel>(json, ReadOptions);
                if (keyPairData != null)
                    result[tagPart] = keyPairData;
            }
            catch
            {
                // skip invalid key files
            }
        }

        return result;
    }

    private static void FlattenJson(Dictionary<string, JsonElement> obj, string prefix, Dictionary<string, string> result)
    {
        foreach (var kvp in obj)
        {
            var key = prefix.Length == 0 ? kvp.Key : $"{prefix}:{kvp.Key}";
            FlattenElement(kvp.Value, key, result);
        }
    }

    private static void FlattenElement(JsonElement element, string key, Dictionary<string, string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    FlattenElement(prop.Value, $"{key}:{prop.Name}", result);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    FlattenElement(item, $"{key}:{index}", result);
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
