using System;
using Deneblab.StashLock.Cli.Models;

namespace Deneblab.StashLock.Cli.Common.Security;

/// <summary>Where a resolved API key came from.</summary>
internal enum ApiKeySource
{
    Flag,
    Environment,
    Config
}

/// <summary>A resolved API key and its source.</summary>
internal sealed record ResolvedApiKey(string Value, ApiKeySource Source);

/// <summary>
/// Resolves the server URL and API key. Order: command-line flag, then environment, then config file
/// (<c>ServerUrl</c>, or <c>ApiKeys[&lt;tag&gt;]</c> of stashlock.config.json).
/// </summary>
internal static class ServerSettings
{
    public const string UrlEnvVar = "STASHLOCK_API_URL";
    public const string ApiKeyEnvVar = "STASHLOCK_API_KEY";

    public static string? ResolveUrl(string? flag, StashlockConfig? config)
    {
        if (!string.IsNullOrWhiteSpace(flag))
            return flag;
        var env = Environment.GetEnvironmentVariable(UrlEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
            return env;
        return string.IsNullOrWhiteSpace(config?.ServerUrl) ? null : config.ServerUrl;
    }

    /// <summary>
    /// Flag, then <see cref="ApiKeyEnvVar"/>, then <c>ApiKeys[tag]</c> of the config (tag case-insensitive).
    /// Returns null when none is set.
    /// </summary>
    public static ResolvedApiKey? ResolveApiKey(string? flag, StashlockConfig? config = null, string? tag = null)
    {
        if (!string.IsNullOrWhiteSpace(flag))
            return new ResolvedApiKey(flag, ApiKeySource.Flag);
        var env = Environment.GetEnvironmentVariable(ApiKeyEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
            return new ResolvedApiKey(env, ApiKeySource.Environment);
        if (!string.IsNullOrWhiteSpace(tag)
            && config?.ApiKeys != null
            && config.ApiKeys.TryGetValue(tag, out var fromConfig)
            && !string.IsNullOrWhiteSpace(fromConfig))
            return new ResolvedApiKey(fromConfig, ApiKeySource.Config);
        return null;
    }

    /// <summary>Text for "no API key" errors.</summary>
    public static string ApiKeyHint(string? tag) =>
        $"use --api-key, set {ApiKeyEnvVar}, or add ApiKeys.{(string.IsNullOrWhiteSpace(tag) ? "<tag>" : tag)} to {VaultConfig.FileName}";

    /// <summary>
    /// Warns on stderr when a key was read from stashlock.config.json and that file is not listed
    /// in the vault directory's .gitignore (the key would end up in the repository).
    /// </summary>
    public static void WarnIfConfigKeyNotIgnored(ResolvedApiKey? key, string vaultDir)
    {
        if (key?.Source != ApiKeySource.Config || GitIgnore.IsIgnored(vaultDir, GitIgnore.ConfigFilePattern))
            return;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Error.WriteLine($"WARNING: {VaultConfig.FileName} holds an API key but is not in .gitignore. Add it, or the key may be committed.");
        Console.ResetColor();
    }
}
