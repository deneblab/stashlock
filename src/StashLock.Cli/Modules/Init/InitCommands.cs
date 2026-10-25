using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ConsoleAppFramework;
using Deneblab.StashLock.Cli.Common;
using Deneblab.StashLock.Cli.Common.Security;
using Deneblab.StashLock.Cli.Models;
using Deneblab.StashLock.Cli.Modules.Ecies;
using Microsoft.Extensions.Logging;

namespace Deneblab.StashLock.Cli.Modules.Init;

internal class InitCommands
{
    private readonly ILogger<InitCommands> _log;

    public InitCommands(ILogger<InitCommands> log)
    {
        _log = log;
    }

    /// <summary>
    /// Initialize a directory as a StashLock vault.
    ///
    /// Examples:
    ///   stashlock init ./my-vault
    ///   stashlock init ./my-vault --tags production,develop
    ///   stashlock init ./my-vault --tags production --url https://vault.example.com
    /// </summary>
    /// <param name="dir">Directory path to initialize as a StashLock vault</param>
    /// <param name="tags">-t, Comma-separated list of environment tags (e.g., production,develop)</param>
    /// <param name="url">Server URL to store in config for publish command</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [Command("")]
    public Task<int> Root(
        [Argument] string dir,
        string tags = "",
        string url = "",
        CancellationToken cancellationToken = default)
    {
        var targetDir = Path.IsPathFullyQualified(dir)
            ? dir
            : Path.GetFullPath(dir);

        _log.LogInformation("Initializing StashLock vault at: {Dir}", targetDir);

        if (!Directory.Exists(targetDir))
        {
            _log.LogInformation("Creating directory: {Dir}", targetDir);
            Directory.CreateDirectory(targetDir);
        }

        var configPath = Path.Combine(targetDir, "stashlock.config.json");
        if (File.Exists(configPath))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Error.WriteLine($"Warning: stashlock.config.json already exists at {configPath}");
            Console.Error.WriteLine($"Skipping — to reinitialize, delete the file first:");
            Console.Error.WriteLine($"  del \"{configPath}\"");
            Console.ResetColor();
            _log.LogWarning("Config already exists at {Path}, skipping", configPath);
            return Task.FromResult(ExitCodes.Error);
        }

        var tagList = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(tags))
        {
            foreach (var t in tags.Split(','))
            {
                var trimmed = t.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    tagList.Add(trimmed);
            }
        }

        var dirName = new DirectoryInfo(Directory.GetCurrentDirectory()).Name;
        var derivedName = new string(dirName.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (string.IsNullOrEmpty(derivedName))
            derivedName = "vault";

        var config = new StashlockConfig
        {
            Version = "00001",
            Name = derivedName,
            Tags = tagList,
            EncryptionMode = "sops",
            ServerUrl = url
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var json = JsonSerializer.Serialize(config, options);
        File.WriteAllText(configPath, json);

        _log.LogInformation("Created stashlock.config.json at {Path}", configPath);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Initialized vault at: {targetDir}");
        Console.WriteLine($"Created: {configPath}");
        if (tagList.Count > 0)
            Console.WriteLine($"Tags: {string.Join(", ", tagList)}");
        Console.ResetColor();

        // Auto-generate key files for each tag
        if (tagList.Count > 0)
        {
            Console.WriteLine();
            var created = 0;
            foreach (var tag in tagList)
            {
                var keyFilePath = Path.Combine(targetDir, $"stashlock.key.{tag}.json");
                if (File.Exists(keyFilePath))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine($"Skipping {tag}: key file already exists");
                    Console.ResetColor();
                    continue;
                }

                var keyPair = EciesModule.GenerateKeyPair(tag);
                var keyJson = JsonSerializer.Serialize(keyPair, options);
                SecureFile.WriteAllText(keyFilePath, keyJson);

                _log.LogInformation("Generated key file for tag {Tag} at {Path}", tag, keyFilePath);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Created: stashlock.key.{tag}.json");
                Console.ResetColor();
                created++;
            }

            if (created > 0)
            {
                if (GitIgnore.EnsureKeyFilesIgnored(targetDir))
                    Console.WriteLine($"Updated .gitignore: {GitIgnore.KeyFilePattern}");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine("WARNING: Keep key files secure. Do NOT commit them to version control.");
                Console.ResetColor();
            }

            Console.WriteLine();
            Console.WriteLine($"Next: ask a StashLock admin for an API key per tag ({string.Join(", ", tagList.Select(t => $"{derivedName}.{t}"))})");
            Console.WriteLine($"      and set it as {ServerSettings.ApiKeyEnvVar} for publish and your app.");
        }
        else
        {
            Console.WriteLine("No tags defined. Edit stashlock.config.json to add tags, then run 'stashlock keygen'.");
        }

        return Task.FromResult(ExitCodes.Ok);
    }
}
