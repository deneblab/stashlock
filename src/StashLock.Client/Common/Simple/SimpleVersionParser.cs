using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Deneblab.StashLock.Client.Common.Simple;

/// <summary>
///     Strongly typed representation of product + AssemblyInformationalVersion metadata.
/// </summary>
public record SimpleVersionInfo(
    string Product, // From [assembly: AssemblyProduct(...)]
    string SemVer, // Base version before '+'
    int BuildCounter, // Parsed or 0 if missing/invalid
    string Branch, // Parsed or empty if missing
    DateTime DateTime, // Parsed or DateTime.MinValue if missing/invalid
    string Env, // Parsed or empty if missing
    string Sha, // Parsed or empty if missing
    int GitCommits, // Parsed or 0 if missing/invalid
    Dictionary<string, string> Extra // All raw key-value metadata (includes known keys)
);

public static class SimpleVersionParser
{
    /// <summary>
    ///     Convenience helper for the main app (works with PublishSingleFile).
    /// </summary>
    public static SimpleVersionInfo FromCurrentApp()
    {
        return FromAssembly(Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly());
    }

    /// <summary>
    ///     Extracts Product + AssemblyInformationalVersion from the given assembly and parses it.
    /// </summary>
    public static SimpleVersionInfo FromAssembly(Assembly assembly)
    {
        if (assembly == null) throw new ArgumentNullException(nameof(assembly));

        // Read [AssemblyProduct]
        var product = assembly
            .GetCustomAttributes(typeof(AssemblyProductAttribute), false)
            .OfType<AssemblyProductAttribute>()
            .FirstOrDefault()?.Product ?? string.Empty;

        // Read [AssemblyInformationalVersion]
        var infoAttr = assembly
                           .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                           .OfType<AssemblyInformationalVersionAttribute>()
                           .FirstOrDefault()
                       ?? throw new InvalidOperationException("AssemblyInformationalVersion attribute not found.");

        return ParseInternal(product, infoAttr.InformationalVersion);
    }

    /// <summary>
    ///     Parses a raw informational version string into SimpleVersionInfo.
    ///     Example:
    ///     "2.3.177+BuildCounter.4941.Branch.production.DateTime.2025-07-11T10:28:49Z.Env.SCRAPER-12.Sha.abc.GitCommits.2971"
    /// </summary>
    public static SimpleVersionInfo Parse(string informationalVersion)
    {
        return ParseInternal(string.Empty, informationalVersion);
    }

    // ---- Implementation ----

    private static SimpleVersionInfo ParseInternal(string product, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentNullException(nameof(input));

        // Split into base semver and metadata (everything after '+')
        var parts = input.Split('+', 2);
        var semver = parts[0];
        var metadata = parts.Length > 1 ? parts[1] : "";

        // Regex matches "Key.Value" pairs separated by dots.
        // Example: "BuildCounter.4941" -> Key="BuildCounter", Value="4941"
        var regex = new Regex(@"([A-Za-z]+)\.([^\.]+)");
        var matches = regex.Matches(metadata);

        // Store all parsed pairs in a dictionary (case-insensitive keys)
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in matches) dict[m.Groups[1].Value] = m.Groups[2].Value;

        // Map known fields with safe defaults (no nullable types)
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
            dict // Preserve all key-value pairs (including known ones)
        );
    }
}