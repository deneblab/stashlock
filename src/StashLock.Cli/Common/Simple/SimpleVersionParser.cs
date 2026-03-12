using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Deneblab.StashLock.Cli.Common.Simple;

public record SimpleVersionInfo(
    string Product,
    string SemVer,
    int BuildCounter,
    string Branch,
    DateTime DateTime,
    string Env,
    string Sha,
    int GitCommits,
    Dictionary<string, string> Extra
);

public static class SimpleVersionParser
{
    public static SimpleVersionInfo FromCurrentApp()
    {
        return FromAssembly(Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly());
    }

    public static SimpleVersionInfo FromAssembly(Assembly assembly)
    {
        if (assembly == null) throw new ArgumentNullException(nameof(assembly));

        var product = assembly
            .GetCustomAttributes(typeof(AssemblyProductAttribute), false)
            .OfType<AssemblyProductAttribute>()
            .FirstOrDefault()?.Product ?? string.Empty;

        var infoAttr = assembly
                           .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                           .OfType<AssemblyInformationalVersionAttribute>()
                           .FirstOrDefault()
                       ?? throw new InvalidOperationException("AssemblyInformationalVersion attribute not found.");

        return ParseInternal(product, infoAttr.InformationalVersion);
    }

    public static SimpleVersionInfo Parse(string informationalVersion)
    {
        return ParseInternal(string.Empty, informationalVersion);
    }

    private static SimpleVersionInfo ParseInternal(string product, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentNullException(nameof(input));

        var parts = input.Split('+', 2);
        var semver = parts[0];
        var metadata = parts.Length > 1 ? parts[1] : "";

        var regex = new Regex(@"([A-Za-z]+)\.([^\.]+)");
        var matches = regex.Matches(metadata);

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in matches) dict[m.Groups[1].Value] = m.Groups[2].Value;

        var buildCounter = dict.TryGetValue("BuildCounter", out var bc) && int.TryParse(bc, out var bcVal) ? bcVal : 0;
        var gitCommits = dict.TryGetValue("GitCommits", out var gc) && int.TryParse(gc, out var gcVal) ? gcVal : 0;
        var dt = dict.TryGetValue("DateTime", out var dts) && DateTime.TryParse(dts, out var dtVal)
            ? dtVal
            : DateTime.MinValue;

        return new SimpleVersionInfo(
            product,
            semver,
            buildCounter,
            dict.TryGetValue("Branch", out var br) ? br : string.Empty,
            dt,
            dict.TryGetValue("Env", out var env) ? env : string.Empty,
            dict.TryGetValue("Sha", out var sha) ? sha : string.Empty,
            gitCommits,
            dict
        );
    }
}
