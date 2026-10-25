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
    ///
    /// A secrets.&lt;tag&gt;.json next to the file overrides its values for that tag.
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

        Dictionary<string, string> secretsDict;
        try
        {
            secretsDict = SecretsOverride.Read(secretFilePath);
        }
        catch (JsonException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: Invalid JSON in secrets file: {ex.Message}");
            Console.ResetColor();
            return Task.FromResult(ExitCodes.Error);
        }

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

        var inputBaseName = Path.GetFileNameWithoutExtension(secretFilePath);
        var tags = new HashSet<string>(config.Tags, StringComparer.OrdinalIgnoreCase);
        tags.UnionWith(keyFiles.Keys);

        var dot = inputBaseName.LastIndexOf('.');
        if (dot > 0 && tags.Contains(inputBaseName[(dot + 1)..])
            && File.Exists(Path.Combine(secretsDir, inputBaseName[..dot] + ".json")))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: {Path.GetFileName(secretFilePath)} overrides {inputBaseName[..dot]}.json for tag '{inputBaseName[(dot + 1)..]}'.");
            Console.Error.WriteLine($"Run 'stashlock encode {Path.Combine(Path.GetDirectoryName(file) ?? "", inputBaseName[..dot] + ".json")}' instead.");
            Console.ResetColor();
            return Task.FromResult(ExitCodes.Error);
        }

        // Read every override before writing anything, so a broken one leaves the vault unchanged.
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var secretsByTag = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in keyFiles.Keys)
        {
            var overrideName = SecretsOverride.FileNameFor(inputBaseName, tag);
            var overridePath = Path.Combine(secretsDir, overrideName);
            if (!File.Exists(overridePath))
            {
                secretsByTag[tag] = secretsDict;
                continue;
            }
            try
            {
                secretsByTag[tag] = SecretsOverride.Merge(secretsDict, SecretsOverride.Read(overridePath));
                overrides[tag] = overrideName;
            }
            catch (JsonException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Error: Invalid JSON in {overrideName}: {ex.Message}");
                Console.Error.WriteLine("No files were written.");
                Console.ResetColor();
                return Task.FromResult(ExitCodes.Error);
            }
        }

        if (verbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Error.WriteLine($"  Config:     {configPath}");
            Console.Error.WriteLine($"  Vault:      {config.Name}");
            Console.Error.WriteLine($"  Version:    {config.Version}");
            Console.Error.WriteLine($"  Key files:  {keyFiles.Count} ({string.Join(", ", keyFiles.Keys)})");
            Console.Error.WriteLine($"  Overrides:  {(overrides.Count == 0 ? "none" : string.Join(", ", overrides.Values))}");
            Console.ResetColor();
            Console.WriteLine();
        }

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
            var outputJson = JsonSerializer.Serialize(SopsCodec.Encrypt(secretsByTag[tag], keyPair.GetPublicKeyBytes(), vaultKey), jsonOptions);

            var outputPath = Path.Combine(secretsDir, outputFileName);
            File.WriteAllText(outputPath, outputJson);

            _log.LogInformation("Generated encrypted file for tag {Tag}: {File}", tag, outputPath);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(overrides.TryGetValue(tag, out var overrideFile)
                ? $"Generated: {outputFileName}  (vault key: {vaultKey}, override: {overrideFile})"
                : $"Generated: {outputFileName}  (vault key: {vaultKey})");
            Console.ResetColor();
            generated++;
        }

        foreach (var tag in config.Tags.Where(t => !keyFiles.ContainsKey(t)))
        {
            var overrideName = SecretsOverride.FileNameFor(inputBaseName, tag);
            if (!File.Exists(Path.Combine(secretsDir, overrideName)))
                continue;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Error.WriteLine($"Warning: {overrideName} skipped: no stashlock.key.{tag}.json in the vault");
            Console.ResetColor();
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
}
