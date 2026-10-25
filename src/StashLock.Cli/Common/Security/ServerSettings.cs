using System;
using Deneblab.StashLock.Cli.Models;

namespace Deneblab.StashLock.Cli.Common.Security;

/// <summary>
/// Resolves the server URL and API key. Order: command-line flag, then environment, then config file.
/// The API key never comes from stashlock.config.json (that file is meant to be committed).
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

    public static string? ResolveApiKey(string? flag)
    {
        if (!string.IsNullOrWhiteSpace(flag))
            return flag;
        var env = Environment.GetEnvironmentVariable(ApiKeyEnvVar);
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }
}
