using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ConsoleAppFramework;
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
    /// </summary>
    [Command("")]
    public Task Root(
        [Argument] string dir,
        string tag = "",
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
            return Task.CompletedTask;
        }

        // 1. Load stashlock.config.json
        var configPath = Path.Combine(targetDir, "stashlock.config.json");
        if (!File.Exists(configPath))
        {
            PrintError($"Error: stashlock.config.json not found in {targetDir}");
            return Task.CompletedTask;
        }

        StashlockConfig config;
        try
        {
            var configJson = File.ReadAllText(configPath);
            config = JsonSerializer.Deserialize<StashlockConfig>(configJson, ReadOptions);
        }
        catch (Exception ex)
        {
            PrintError($"Error reading config: {ex.Message}");
            return Task.CompletedTask;
        }

        if (config == null)
        {
            PrintError("Error: Failed to parse stashlock.config.json");
            return Task.CompletedTask;
        }

        // 2. Display vault info
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  Vault directory: {targetDir}");
        Console.WriteLine($"  Server URL: {config.ServerUrl}");
        Console.WriteLine($"  API Key: {Mask(config.ApiKey)}");
        Console.ResetColor();
        Console.WriteLine();

        // 3. Resolve tag
        var selectedTag = tag;
        if (string.IsNullOrEmpty(selectedTag))
        {
            selectedTag = PromptForTag(config.Tags, targetDir);
            if (selectedTag == null)
                return Task.CompletedTask;
        }

        // 4. Find key file for tag
        var keyFilePath = Path.Combine(targetDir, $"stashlock.key.{selectedTag}.json");
        if (!File.Exists(keyFilePath))
        {
            PrintError($"Error: Key file not found: stashlock.key.{selectedTag}.json");
            return Task.CompletedTask;
        }

        KeyPairModel keyPair;
        try
        {
            var keyJson = File.ReadAllText(keyFilePath);
            keyPair = JsonSerializer.Deserialize<KeyPairModel>(keyJson, ReadOptions);
        }
        catch (Exception ex)
        {
            PrintError($"Error reading key file: {ex.Message}");
            return Task.CompletedTask;
        }

        if (keyPair == null || string.IsNullOrEmpty(keyPair.PrivateKey))
        {
            PrintError($"Error: Invalid key file for tag '{selectedTag}'");
            return Task.CompletedTask;
        }

        // 5. Build connection string
        var plainCs = BuildConnectionString(config, selectedTag, keyPair.PrivateKey);
        var base64Cs = Convert.ToBase64String(Encoding.UTF8.GetBytes(plainCs));

        // 6. Output
        if (env)
        {
            var isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            var prefix = isWindows ? "set" : "export";
            Console.WriteLine($"{prefix} STASHLOCK_CONNECTION_STRING=\"{plainCs}\"");
            return Task.CompletedTask;
        }

        if (base64)
        {
            Console.WriteLine(base64Cs);
            return Task.CompletedTask;
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
        var isWin = Environment.OSVersion.Platform == PlatformID.Win32NT;
        var setCmd = isWin ? "set" : "export";
        Console.WriteLine($"    {setCmd} STASHLOCK_CONNECTION_STRING=\"{plainCs}\"");
        Console.WriteLine();

        // Try clipboard
        TryCopyToClipboard(plainCs);

        return Task.CompletedTask;
    }

    private static string BuildConnectionString(StashlockConfig config, string tag, string privateKey)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(config.ServerUrl))
            parts.Add($"Url={config.ServerUrl}");
        if (!string.IsNullOrEmpty(config.ApiKey))
            parts.Add($"ApiKey={config.ApiKey}");
        if (!string.IsNullOrEmpty(privateKey))
            parts.Add($"PrivateKey={privateKey}");
        if (!string.IsNullOrEmpty(config.Name))
            parts.Add($"Box={config.Name}");
        parts.Add($"Tag={tag}");
        return string.Join(";", parts);
    }

    private static string PromptForTag(List<string> tags, string dir)
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
            Console.WriteLine($"  Tag: {tagList[0]} (only one available)");
            Console.ResetColor();
            return tagList[0];
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  Available tags:");
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

    private static void TryCopyToClipboard(string text)
    {
        try
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c echo {text}| clip",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    }
                };
                process.Start();
                process.WaitForExit(2000);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  Copied plain connection string to clipboard!");
                Console.ResetColor();
            }
        }
        catch
        {
            // Clipboard copy is best-effort
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
        Console.WriteLine(message);
        Console.ResetColor();
    }
}
