using System;
using System.Collections.Generic;

namespace Deneblab.StashLock.Client;

/// <summary>
/// Parses ADO.NET-style connection strings (Key=Value;Key=Value).
/// Supports optional base64-encoded wrapper for safe transport.
/// Only recognizes 5 connection-level keys: Url, ApiKey, PrivateKey, Box, Tag.
/// </summary>
internal static class ConnectionStringParser
{
    private const string ENV_CONNECTION_STRING = "STASHLOCK_CONNECTION_STRING";

    internal static readonly HashSet<string> ValidKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Url", "ApiKey", "PrivateKey", "Box", "Tag"
    };

    /// <summary>
    /// Reads the connection string from the STASHLOCK_CONNECTION_STRING environment variable.
    /// </summary>
    internal static string FromEnvironment()
    {
        var cs = Environment.GetEnvironmentVariable(ENV_CONNECTION_STRING);
        if (string.IsNullOrWhiteSpace(cs))
            throw new Common.Exceptions.VaultConfigurationException(
                ENV_CONNECTION_STRING,
                $"Environment variable {ENV_CONNECTION_STRING} is not set or empty.");
        return cs;
    }

    /// <summary>
    /// Parses a connection string into key-value pairs.
    /// Auto-detects base64-encoded input: if no semicolons are found, tries base64 decode.
    /// </summary>
    internal static Dictionary<string, string> Parse(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentNullException(nameof(connectionString));

        var input = Unwrap(connectionString.Trim());
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Split on semicolons, handling values that may contain '=' (e.g., base64 PrivateKey)
        var pairs = input.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var trimmed = pair.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            // Split only on the FIRST '=' to preserve base64 padding in values
            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex <= 0)
                throw new Common.Exceptions.StashLockException(
                    $"Invalid connection string segment: '{trimmed}'. Expected Key=Value format.");

            var key = trimmed.Substring(0, eqIndex).Trim();
            var value = trimmed.Substring(eqIndex + 1).Trim();

            if (!ValidKeys.Contains(key))
                throw new Common.Exceptions.StashLockException(
                    $"Unknown connection string key: '{key}'. Valid keys are: {string.Join(", ", ValidKeys)}.");

            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Builds a connection string from individual values.
    /// </summary>
    internal static string Build(string url = null, string apiKey = null, string privateKey = null,
        string box = null, string tag = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(url)) parts.Add($"Url={url}");
        if (!string.IsNullOrEmpty(apiKey)) parts.Add($"ApiKey={apiKey}");
        if (!string.IsNullOrEmpty(privateKey)) parts.Add($"PrivateKey={privateKey}");
        if (!string.IsNullOrEmpty(box)) parts.Add($"Box={box}");
        if (!string.IsNullOrEmpty(tag)) parts.Add($"Tag={tag}");
        return string.Join(";", parts);
    }

    /// <summary>
    /// Encodes a plain connection string to base64.
    /// </summary>
    internal static string ToBase64(string plainConnectionString)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(plainConnectionString);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Unwraps a potentially base64-encoded connection string.
    /// Heuristic: if the input contains ';' it's already plain; otherwise try base64 decode.
    /// </summary>
    private static string Unwrap(string input)
    {
        if (input.Contains(';'))
            return input;

        // No semicolons — try base64 decode
        try
        {
            var bytes = Convert.FromBase64String(input);
            var decoded = System.Text.Encoding.UTF8.GetString(bytes);
            if (decoded.Contains(';'))
                return decoded;
        }
        catch (FormatException)
        {
            // Not valid base64, treat as plain
        }

        return input;
    }
}
