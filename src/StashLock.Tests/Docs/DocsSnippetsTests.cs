using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Deneblab.StashLock.Tests.Docs;

/// <summary>
/// Complete C# examples in the docs (blocks that contain a "using Deneblab…" line) must import
/// every StashLock namespace they use, so readers can paste them and compile.
/// </summary>
public class DocsSnippetsTests
{
    private static readonly (Regex Uses, string Using)[] Rules =
    [
        (new Regex(@"\b(VaultConfigurationException|VaultNotFoundException|VaultAccessDeniedException|DecryptionException|StashLockException)\b"),
            "using Deneblab.StashLock.Client.Common.Exceptions;"),
        (new Regex(@"\.AddStashLock\("), "using Deneblab.StashLock.Client.Configuration;"),
        (new Regex(@"\bStashLock\.CreateClient\("), "using Deneblab.StashLock.Client;"),
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    public static IEnumerable<object[]> Docs() =>
        new[] { "README.md", "GETTING_STARTED.md", "src/StashLock.Client/README.md" }.Select(d => new object[] { d });

    [Theory]
    [MemberData(nameof(Docs))]
    public void CompleteExamples_HaveTheirUsings(string doc)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), doc));
        var problems = new List<string>();
        foreach (Match block in Regex.Matches(text, "```csharp\\n(.*?)```", RegexOptions.Singleline))
        {
            var code = block.Groups[1].Value;
            if (!code.Contains("using Deneblab")) continue; // fragment, not a complete example
            foreach (var (uses, needed) in Rules)
                if (uses.IsMatch(code) && !code.Contains(needed))
                    problems.Add($"{doc}: block starting '{code.Split('\n')[0].Trim()}' needs '{needed}'");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
