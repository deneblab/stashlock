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
    /// </summary>
    /// <param name="file">Path to the plaintext JSON secrets file</param>
    /// <param name="verbose">Show resolved configuration before encoding</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [Command("")]
    public Task<int> Root(
        [Argument] string file,
        bool verbose = false,
        CancellationToken cancellationToken = default)
    {
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
        var configPath = VaultConfig.PathIn(secretsDir);
        if (!File.Exists(configPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: stashlock.config.json not found at {configPath}");
            Console.Error.WriteLine("Run 'stashlock init' first to create the configuration.");
            Console.ResetColor();
            return Task.FromResult(ExitCodes.Error);
        }

        StashlockConfig config;
        try
        {
            config = VaultConfig.Load(secretsDir)!;
        }
        catch (JsonException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: Failed to parse stashlock.config.json: {ex.Message}");
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
            Console.Error.WriteLine($"  Key files:  {keyFiles.Count} ({string.Join(", ", keyFiles.Keys)})");
            Console.ResetColor();
            Console.WriteLine();
        }

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
            var outputFileName = $"stashlock.enc.{tag}.{inputBaseName}.json";
            var outputJson = JsonSerializer.Serialize(SopsCodec.Encrypt(secretsDict, keyPair.GetPublicKeyBytes(), vaultKey), jsonOptions);

            var outputPath = Path.Combine(secretsDir, outputFileName);
            File.WriteAllText(outputPath, outputJson);

            _log.LogInformation("Generated encrypted file for tag {Tag}: {File}", tag, outputPath);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Generated: {outputFileName}  (vault key: {vaultKey})");
            Console.ResetColor();
            generated++;
        }

        if (config.Skeleton != null)
        {
            var skeleton = SkeletonWriter.Write(secretsDir, inputBaseName, config.Skeleton);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Generated: {Path.GetFileName(skeleton.Path)}  (key names only, safe to commit)");
            Console.ResetColor();
            if (skeleton.Warnings.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                foreach (var warning in skeleton.Warnings)
                    Console.Error.WriteLine($"Warning: {warning}");
                Console.ResetColor();
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Encode complete: {generated} encrypted file(s) created.");
        return Task.FromResult(ExitCodes.Ok);
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
