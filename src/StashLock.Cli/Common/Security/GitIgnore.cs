using System;
using System.IO;
using System.Linq;

namespace Deneblab.StashLock.Cli.Common.Security;

/// <summary>Keeps private key files and the config (which may hold API keys) out of version control.</summary>
internal static class GitIgnore
{
    public const string KeyFilePattern = "stashlock.key.*.json";
    public const string ConfigFilePattern = VaultConfig.FileName;

    /// <summary>Adds <see cref="KeyFilePattern"/> when missing. Returns true when .gitignore was changed.</summary>
    public static bool EnsureKeyFilesIgnored(string directory) =>
        EnsureIgnored(directory, KeyFilePattern, "# StashLock private keys");

    /// <summary>Adds <see cref="ConfigFilePattern"/> when missing. Returns true when .gitignore was changed.</summary>
    public static bool EnsureConfigIgnored(string directory) =>
        EnsureIgnored(directory, ConfigFilePattern, "# StashLock config (may hold API keys)");

    /// <summary>True when the directory's .gitignore has a line equal to <paramref name="pattern"/>.</summary>
    public static bool IsIgnored(string directory, string pattern)
    {
        var path = Path.Combine(directory, ".gitignore");
        return File.Exists(path) && HasLine(File.ReadAllText(path), pattern);
    }

    /// <summary>
    /// Creates .gitignore in the directory or appends the pattern (with a comment line) when missing.
    /// Returns true when the file was changed.
    /// </summary>
    private static bool EnsureIgnored(string directory, string pattern, string comment)
    {
        var path = Path.Combine(directory, ".gitignore");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, $"{comment}{Environment.NewLine}{pattern}{Environment.NewLine}");
            return true;
        }

        var content = File.ReadAllText(path);
        if (HasLine(content, pattern))
            return false;

        var prefix = content.Length > 0 && !content.EndsWith('\n') ? Environment.NewLine : string.Empty;
        File.AppendAllText(path, $"{prefix}{comment}{Environment.NewLine}{pattern}{Environment.NewLine}");
        return true;
    }

    private static bool HasLine(string content, string pattern) =>
        content.Split('\n').Select(l => l.Trim()).Contains(pattern);
}
