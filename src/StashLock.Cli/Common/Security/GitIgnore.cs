using System;
using System.IO;
using System.Linq;

namespace Deneblab.StashLock.Cli.Common.Security;

/// <summary>Keeps private key files out of version control.</summary>
internal static class GitIgnore
{
    public const string KeyFilePattern = "stashlock.key.*.json";

    /// <summary>
    /// Creates .gitignore in the directory or appends the key file pattern when missing.
    /// Returns true when the file was changed.
    /// </summary>
    public static bool EnsureKeyFilesIgnored(string directory)
    {
        var path = Path.Combine(directory, ".gitignore");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, $"# StashLock private keys{Environment.NewLine}{KeyFilePattern}{Environment.NewLine}");
            return true;
        }

        var content = File.ReadAllText(path);
        if (content.Split('\n').Select(l => l.Trim()).Contains(KeyFilePattern))
            return false;

        var prefix = content.Length > 0 && !content.EndsWith('\n') ? Environment.NewLine : string.Empty;
        File.AppendAllText(path, $"{prefix}# StashLock private keys{Environment.NewLine}{KeyFilePattern}{Environment.NewLine}");
        return true;
    }
}
