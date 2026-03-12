using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ConsoleAppFramework;
using Deneblab.StashLock.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Deneblab.StashLock.Cli.Modules.Publish;

internal class PublishCommands
{
    private const string DefaultApiUrl = "https://deneblabvault.azurewebsites.net/api/";

    private readonly ILogger<PublishCommands> _log;

    public PublishCommands(ILogger<PublishCommands> log)
    {
        _log = log;
    }

    /// <summary>
    /// Publish encrypted secrets to the StashLock server.
    ///
    /// Examples:
    ///   stashlock publish ./my-vault
    ///   stashlock publish ./my-vault/stashlock.enc.production.secrets.json
    ///   stashlock publish ./my-vault --url https://vault.example.com --api-key mykey123
    ///   stashlock publish ./my-vault --verbose --expires-in 3600
    /// </summary>
    /// <param name="path">Path to stashlock.enc.*.json file or directory containing them</param>
    /// <param name="url">API base URL (falls back to STASHLOCK_API_URL env var)</param>
    /// <param name="apiKey">API authentication key (falls back to STASHLOCK_API_KEY env var)</param>
    /// <param name="expiresIn">TTL in seconds for the published secret</param>
    /// <param name="verbose">Show resolved configuration before publishing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [Command("")]
    public async Task Root(
        [Argument] string path,
        string url = "",
        string apiKey = "",
        int? expiresIn = null,
        bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        // Resolve files
        var fullPath = Path.IsPathFullyQualified(path)
            ? path
            : Path.GetFullPath(path);

        string[] files;
        if (File.Exists(fullPath))
        {
            files = [fullPath];
        }
        else if (Directory.Exists(fullPath))
        {
            files = Directory.GetFiles(fullPath, "stashlock.enc.*.json");
            if (files.Length == 0)
            {
                PrintError($"Error: No stashlock.enc.*.json files found in {fullPath}");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Hint: Run 'stashlock encode <secrets.json>' first to generate encrypted files.");
                Console.ResetColor();
                return;
            }
        }
        else
        {
            PrintError($"Error: Path not found: {fullPath}");
            return;
        }

        // Try to load config from vault directory
        var configDir = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath)! : fullPath;
        var config = LoadConfig(configDir);

        // Resolve API URL: flag > config > env var > default
        var apiUrl = !string.IsNullOrEmpty(url) ? url
            : !string.IsNullOrEmpty(config?.ServerUrl) ? config.ServerUrl
            : Environment.GetEnvironmentVariable("STASHLOCK_API_URL") ?? DefaultApiUrl;

        // Resolve API key: flag > config > env var
        var resolvedApiKey = !string.IsNullOrEmpty(apiKey) ? apiKey
            : !string.IsNullOrEmpty(config?.ApiKey) ? config.ApiKey
            : Environment.GetEnvironmentVariable("STASHLOCK_API_KEY");

        if (string.IsNullOrEmpty(resolvedApiKey))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Warning: No API key provided. Use --api-key or set STASHLOCK_API_KEY.");
            Console.ResetColor();
        }

        if (verbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  API URL:    {apiUrl}");
            Console.WriteLine($"  API Key:    {(string.IsNullOrEmpty(resolvedApiKey) ? "(none)" : resolvedApiKey[..Math.Min(8, resolvedApiKey.Length)] + "...")}");
            Console.WriteLine($"  Config:     {(config != null ? Path.Combine(configDir, "stashlock.config.json") : "(not found)")}");
            Console.WriteLine($"  Files:      {files.Length}");
            if (expiresIn.HasValue)
                Console.WriteLine($"  TTL:        {expiresIn}s");
            Console.ResetColor();
            Console.WriteLine();
        }

        _log.LogInformation("Publishing to {Url}, {Count} file(s)", apiUrl, files.Length);

        var published = 0;
        var failed = 0;

        using var client = new StashLockApiClient(apiUrl, resolvedApiKey);

        for (var i = 0; i < files.Length; i++)
        {
            var file = files[i];
            var fileName = Path.GetFileName(file);
            if (files.Length > 1)
                Console.Write($"[{i + 1}/{files.Length}] ");
            var content = await File.ReadAllTextAsync(file, cancellationToken);

            var vaultKey = VaultKeyExtractor.Extract(content);
            if (string.IsNullOrEmpty(vaultKey))
            {
                PrintError($"  {fileName}: Could not extract vault key — skipped");
                failed++;
                continue;
            }

            _log.LogInformation("Publishing {File} with vault key {VaultKey}", fileName, vaultKey);

            var result = await client.PublishAsync(vaultKey, content, expiresIn, cancellationToken);

            if (result.IsSuccess)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  {fileName}: Published (key: {result.ServerKey})");
                Console.ResetColor();
                published++;
            }
            else
            {
                PrintError($"  {fileName}: Failed (HTTP {result.StatusCode}) — {result.ErrorDetail}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Publish complete: {published} published, {failed} failed.");
    }

    private static StashlockConfig? LoadConfig(string directory)
    {
        var configPath = Path.Combine(directory, "stashlock.config.json");
        if (!File.Exists(configPath))
            return null;

        var json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<StashlockConfig>(json, new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
    }

    private static void PrintError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }
}
