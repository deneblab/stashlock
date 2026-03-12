using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ConsoleAppFramework;
using Deneblab.StashLock.Cli.Models;
using Deneblab.StashLock.Cli.Modules.Ecies;
using Microsoft.Extensions.Logging;

namespace Deneblab.StashLock.Cli.Modules.Keygen;

internal class KeyCommands
{
    private readonly ILogger<KeyCommands> _log;

    public KeyCommands(ILogger<KeyCommands> log)
    {
        _log = log;
    }

    /// <summary>
    /// Generate encryption key files for each tag defined in stashlock.config.json.
    ///
    /// Examples:
    ///   stashlock keygen ./my-vault
    ///   stashlock keygen .
    /// </summary>
    /// <param name="dir">Directory containing stashlock.config.json</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [Command("")]
    public Task Root(
        [Argument] string dir,
        CancellationToken cancellationToken = default)
    {
        var targetDir = Path.IsPathFullyQualified(dir)
            ? dir
            : Path.GetFullPath(dir);

        var configPath = Path.Combine(targetDir, "stashlock.config.json");
        if (!File.Exists(configPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: stashlock.config.json not found at {configPath}");
            Console.WriteLine("Run 'stashlock init' first to create the configuration.");
            Console.ResetColor();
            _log.LogError("Config not found at {Path}", configPath);
            return Task.CompletedTask;
        }

        var configJson = File.ReadAllText(configPath);
        var readOptions = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        var config = JsonSerializer.Deserialize<StashlockConfig>(configJson, readOptions);
        if (config == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Failed to parse stashlock.config.json at {configPath}");
            Console.ResetColor();
            return Task.CompletedTask;
        }

        if (config.Tags == null || config.Tags.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("No tags defined in stashlock.config.json.");
            Console.WriteLine("Run 'stashlock init /path -tags production,develop' to define tags.");
            Console.ResetColor();
            _log.LogWarning("No tags defined in config at {Path}", configPath);
            return Task.CompletedTask;
        }

        _log.LogInformation("Generating key files for {Count} tags in {Dir}", config.Tags.Count, targetDir);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var created = 0;
        var skipped = 0;

        foreach (var tag in config.Tags)
        {
            var keyFilePath = Path.Combine(targetDir, $"stashlock.key.{tag}.json");
            if (File.Exists(keyFilePath))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Skipping {tag}: key file already exists at {keyFilePath}");
                Console.ResetColor();
                _log.LogWarning("Key file already exists for tag {Tag} at {Path}", tag, keyFilePath);
                skipped++;
                continue;
            }

            var keyPair = EciesModule.GenerateKeyPair(tag);
            var keyJson = JsonSerializer.Serialize(keyPair, options);
            File.WriteAllText(keyFilePath, keyJson);

            _log.LogInformation("Generated key file for tag {Tag} at {Path}", tag, keyFilePath);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Created: stashlock.key.{tag}.json");
            Console.ResetColor();
            created++;
        }

        Console.WriteLine();
        Console.WriteLine($"Key generation complete: {created} created, {skipped} skipped.");
        if (created > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("WARNING: Keep key files secure. Do NOT commit them to version control.");
            Console.ResetColor();
        }

        return Task.CompletedTask;
    }
}
