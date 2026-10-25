using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ConsoleAppFramework;
using Deneblab.StashLock.Cli.Common;
using Deneblab.StashLock.Cli.Common.Security;
using Deneblab.StashLock.Cli.Models;
using Deneblab.StashLock.Cli.Modules.Ecies;
using Microsoft.Extensions.Logging;

namespace Deneblab.StashLock.Cli.Modules.CString;

/// <summary>
/// Generate a connection string from a StashLock vault directory.
/// </summary>
internal class CstringCommands
{
    private readonly ILogger<CstringCommands> _log;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public CstringCommands(ILogger<CstringCommands> log)
    {
        _log = log;
    }

    /// <summary>
    /// Generate a StashLock connection string from a vault directory.
    ///
    /// Examples:
    ///   stashlock cstring ./my-vault
    ///   stashlock cstring ./my-vault --tag production
    ///   stashlock cstring ./my-vault --tag production --base64
    ///   stashlock cstring ./my-vault --tag production --env
    ///   stashlock cstring ./my-vault --tag production --api-key slk_...
    /// </summary>
    /// <param name="dir">Vault directory containing stashlock.config.json</param>
    /// <param name="tag">Tag to build the connection string for</param>
    /// <param name="apiKey">API key for box.tag (falls back to STASHLOCK_API_KEY env var)</param>
    /// <param name="base64">Print only the base64-encoded connection string</param>
    /// <param name="env">Print only the environment variable assignment</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [Command("")]
    public Task<int> Root(
        [Argument] string dir,
        string tag = "",
        string apiKey = "",
        bool base64 = false,
        bool env = false,
        CancellationToken cancellationToken = default)
    {
        var targetDir = Path.IsPathFullyQualified(dir)
            ? dir
            : Path.GetFullPath(dir);

        if (!Directory.Exists(targetDir))
        {
            PrintError($"Error: Directory not found: {targetDir}");
            return Task.FromResult(ExitCodes.Error);
        }

        // 1. Load stashlock.config.json
        var configPath = VaultConfig.PathIn(targetDir);
        if (!File.Exists(configPath))
        {
            PrintError($"Error: stashlock.config.json not found in {targetDir}");
            return Task.FromResult(ExitCodes.Error);
        }

        StashlockConfig? config;
        try
        {
            config = VaultConfig.Load(targetDir);
        }
        catch (Exception ex)
        {
            PrintError($"Error reading config: {ex.Message}");
            return Task.FromResult(ExitCodes.Error);
        }

        if (config == null)
        {
            PrintError("Error: Failed to parse stashlock.config.json");
            return Task.FromResult(ExitCodes.Error);
        }

        // 2. Display vault info
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Error.WriteLine($"  Vault directory: {targetDir}");
        Console.Error.WriteLine($"  Server URL: {config.ServerUrl}");
        var resolvedApiKey = ServerSettings.ResolveApiKey(apiKey);
        Console.Error.WriteLine($"  API Key: {(resolvedApiKey == null ? $"(none — use --api-key or {ServerSettings.ApiKeyEnvVar})" : Mask(resolvedApiKey))}");
        Console.ResetColor();
        Console.Error.WriteLine();

        // 3. Resolve tag
        var selectedTag = tag;
        if (string.IsNullOrEmpty(selectedTag))
        {
            selectedTag = PromptForTag(config.Tags, targetDir);
            if (selectedTag == null)
                return Task.FromResult(ExitCodes.Error);
        }

        // 4. Find key file for tag
        var keyFilePath = Path.Combine(targetDir, $"stashlock.key.{selectedTag}.json");
        if (!File.Exists(keyFilePath))
        {
            PrintError($"Error: Key file not found: stashlock.key.{selectedTag}.json");
            return Task.FromResult(ExitCodes.Error);
        }

        KeyPairModel? keyPair;
        try
        {
            var keyJson = File.ReadAllText(keyFilePath);
            keyPair = JsonSerializer.Deserialize<KeyPairModel>(keyJson, ReadOptions);
        }
        catch (Exception ex)
        {
            PrintError($"Error reading key file: {ex.Message}");
            return Task.FromResult(ExitCodes.Error);
        }

        if (keyPair == null || string.IsNullOrEmpty(keyPair.PrivateKey))
        {
            PrintError($"Error: Invalid key file for tag '{selectedTag}'");
            return Task.FromResult(ExitCodes.Error);
        }

        // 5. Build connection string
        var plainCs = BuildConnectionString(config, resolvedApiKey, selectedTag, keyPair.PrivateKey);
        var base64Cs = Convert.ToBase64String(Encoding.UTF8.GetBytes(plainCs));

        // 6. Output
        if (env)
        {
            Console.WriteLine(EnvAssignment(plainCs));
            return Task.FromResult(ExitCodes.Ok);
        }

        if (base64)
        {
            Console.WriteLine(base64Cs);
            return Task.FromResult(ExitCodes.Ok);
        }

        // Default: show both
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  --- Connection String ---");
        Console.ResetColor();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("  Plain: ");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"    {plainCs}");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("  Base64: ");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"    {base64Cs}");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("  Environment variable (copy-paste): ");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"    {EnvAssignment(plainCs)}");
        Console.WriteLine();

        // Try clipboard
        TryCopyToClipboard(plainCs);

        return Task.FromResult(ExitCodes.Ok);
    }

    private static string BuildConnectionString(StashlockConfig config, string? apiKey, string tag, string privateKey)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(config.ServerUrl))
            parts.Add($"Url={config.ServerUrl}");
        if (!string.IsNullOrEmpty(apiKey))
            parts.Add($"ApiKey={apiKey}");
        if (!string.IsNullOrEmpty(privateKey))
            parts.Add($"PrivateKey={privateKey}");
        if (!string.IsNullOrEmpty(config.Name))
            parts.Add($"Box={config.Name}");
        parts.Add($"Tag={tag}");
        return string.Join(";", parts);
    }

    private static string? PromptForTag(List<string> tags, string dir)
    {
        // Also discover tags from key files on disk
        var keyFileTags = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "stashlock.key.*.json"))
        {
            var fileName = Path.GetFileName(file);
            var tagPart = fileName.Substring("stashlock.key.".Length,
                fileName.Length - "stashlock.key.".Length - ".json".Length);
            if (!string.IsNullOrEmpty(tagPart))
                keyFileTags.Add(tagPart);
        }

        // Merge config tags + discovered tags
        var allTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tags) allTags.Add(t);
        foreach (var t in keyFileTags) allTags.Add(t);

        var tagList = new List<string>(allTags);
        if (tagList.Count == 0)
        {
            PrintError("Error: No tags found in config or key files.");
            return null;
        }

        if (tagList.Count == 1)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Error.WriteLine($"  Tag: {tagList[0]} (only one available)");
            Console.ResetColor();
            return tagList[0];
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Error.WriteLine("  Available tags:");
        Console.ResetColor();
        for (var i = 0; i < tagList.Count; i++)
        {
            Console.WriteLine($"    [{i + 1}] {tagList[i]}");
        }

        Console.Write("  Select tag [1]: ");
        var input = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(input))
            return tagList[0];

        if (int.TryParse(input, out var index) && index >= 1 && index <= tagList.Count)
            return tagList[index - 1];

        // Try matching by name
        foreach (var t in tagList)
        {
            if (t.Equals(input, StringComparison.OrdinalIgnoreCase))
                return t;
        }

        PrintError($"Error: Invalid selection '{input}'");
        return null;
    }

    // cmd.exe: set "NAME=value" keeps quotes out of the value; POSIX shells: export NAME="value"
    private static string EnvAssignment(string value) =>
        OperatingSystem.IsWindows()
            ? $"set \"STASHLOCK_CONNECTION_STRING={value}\""
            : $"export STASHLOCK_CONNECTION_STRING=\"{value}\"";

    // The secret goes through stdin, never on a command line other processes can read
    private static void TryCopyToClipboard(string text)
    {
        var tools = OperatingSystem.IsWindows() ? new[] { ("clip", "") }
            : OperatingSystem.IsMacOS() ? new[] { ("pbcopy", "") }
            : new[] { ("wl-copy", ""), ("xclip", "-selection clipboard"), ("xsel", "--clipboard --input") };

        foreach (var (file, args) in tools)
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (process == null) continue;
                process.StandardInput.Write(text);
                process.StandardInput.Close();
                if (!process.WaitForExit(2000) || process.ExitCode != 0) continue;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.Error.WriteLine("  Copied plain connection string to clipboard.");
                Console.ResetColor();
                return;
            }
            catch
            {
                // Tool not installed: try the next one (clipboard copy is best-effort)
            }
        }
    }

    private static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value)) return "(not set)";
        if (value.Length <= 6) return new string('*', value.Length);
        return value.Substring(0, 3) + new string('*', value.Length - 6) + value.Substring(value.Length - 3);
    }

    private static void PrintError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(message);
        Console.ResetColor();
    }
}
