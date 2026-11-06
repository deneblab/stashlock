using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ConsoleAppFramework;
using Deneblab.StashLock.Cli.Common;
using Deneblab.StashLock.Cli.Common.Security;
using Deneblab.StashLock.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Deneblab.StashLock.Cli.Modules.Publish;

internal class PublishCommands
{
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
    ///
    /// The API key is chosen per file: --api-key, then STASHLOCK_API_KEY, then ApiKeys.<tag>
    /// in stashlock.config.json (tag taken from the file's vault key).
    /// </summary>
    /// <param name="path">Path to stashlock.enc.*.json file or directory containing them</param>
    /// <param name="url">API base URL (falls back to STASHLOCK_API_URL env var, then ServerUrl in stashlock.config.json)</param>
    /// <param name="apiKey">API key for the box.tag (falls back to STASHLOCK_API_KEY env var, then ApiKeys.&lt;tag&gt; in stashlock.config.json)</param>
    /// <param name="expiresIn">TTL in seconds for the published secret</param>
    /// <param name="verbose">Show resolved configuration before publishing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [Command("")]
    public async Task<int> Root(
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
                Console.Error.WriteLine("Hint: Run 'stashlock encode <secrets.json>' first to generate encrypted files.");
                Console.ResetColor();
                return ExitCodes.Error;
            }
        }
        else
        {
            PrintError($"Error: Path not found: {fullPath}");
            return ExitCodes.Error;
        }

        // Try to load config from vault directory
        var configDir = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath)! : fullPath;
        StashlockConfig? config;
        try
        {
            config = VaultConfig.Load(configDir);
        }
        catch (JsonException ex)
        {
            PrintError($"Error: Failed to parse {VaultConfig.PathIn(configDir)}: {ex.Message}");
            return ExitCodes.Error;
        }

        // Resolve API URL: flag > env var > config
        var apiUrl = ServerSettings.ResolveUrl(url, config);
        if (apiUrl == null)
        {
            PrintError($"Error: No server URL. Use --url, set {ServerSettings.UrlEnvVar}, or add ServerUrl to stashlock.config.json.");
            return ExitCodes.Error;
        }

        // Resolve API key: flag > env var > ApiKeys.<tag> in the config (per file, below)
        var sharedKey = ServerSettings.ResolveApiKey(apiKey);
        if (sharedKey == null && config?.ApiKeys is not { Count: > 0 })
        {
            PrintError($"Error: No API key — {ServerSettings.ApiKeyHint(null)} (an API key created for the box.tag).");
            return ExitCodes.Error;
        }

        if (verbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Error.WriteLine($"  API URL:    {apiUrl}");
            if (sharedKey != null)
                Console.Error.WriteLine($"  API Key:    {Mask(sharedKey.Value)} ({SourceName(sharedKey.Source)})");
            else
                Console.Error.WriteLine($"  API Keys:   per tag from {VaultConfig.FileName} ({string.Join(", ", config!.ApiKeys!.Keys)})");
            Console.Error.WriteLine($"  Config:     {(config != null ? VaultConfig.PathIn(configDir) : "(not found)")}");
            Console.Error.WriteLine($"  Files:      {files.Length}");
            if (expiresIn.HasValue)
                Console.Error.WriteLine($"  TTL:        {expiresIn}s");
            Console.ResetColor();
            Console.WriteLine();
        }

        _log.LogInformation("Publishing to {Url}, {Count} file(s)", apiUrl, files.Length);

        var published = 0;
        var failed = 0;
        var warnedAboutConfigKey = false;

        // One client per distinct API key (files of several tags can be published in one run)
        var clients = new Dictionary<string, StashLockApiClient>(StringComparer.Ordinal);
        try
        {
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

                var fileTag = VaultKeyExtractor.TagOf(vaultKey);
                var key = sharedKey ?? ServerSettings.ResolveApiKey(null, config, fileTag);
                if (key == null)
                {
                    PrintError($"  {fileName}: No API key for tag '{fileTag}' — {ServerSettings.ApiKeyHint(fileTag)}");
                    failed++;
                    continue;
                }

                if (!warnedAboutConfigKey && key.Source == ApiKeySource.Config)
                {
                    ServerSettings.WarnIfConfigKeyNotIgnored(key, configDir);
                    warnedAboutConfigKey = true;
                }

                if (!clients.TryGetValue(key.Value, out var client))
                    clients[key.Value] = client = new StashLockApiClient(apiUrl, key.Value);

                _log.LogInformation("Publishing {File} with vault key {VaultKey} (API key from {Source})", fileName, vaultKey, SourceName(key.Source));

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
        }
        finally
        {
            foreach (var c in clients.Values)
                c.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine($"Publish complete: {published} published, {failed} failed.");
        return failed == 0 ? ExitCodes.Ok : published > 0 ? ExitCodes.Partial : ExitCodes.Error;
    }

    private static string Mask(string key) => $"{key[..Math.Min(8, key.Length)]}...";

    private static string SourceName(ApiKeySource source) => source switch
    {
        ApiKeySource.Flag => "--api-key",
        ApiKeySource.Environment => ServerSettings.ApiKeyEnvVar,
        _ => VaultConfig.FileName
    };

    private static void PrintError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(message);
        Console.ResetColor();
    }
}
