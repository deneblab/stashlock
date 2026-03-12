using System.Collections.Generic;

namespace Deneblab.StashLock.Client;

/// <summary>
/// Result of a configuration validation check via <see cref="StashLockBuilder.ValidateAsync"/>.
/// </summary>
public class ConfigValidationResult
{
    /// <summary>
    /// True if all checks passed and the configuration is ready to use.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// The resolved API URL that will be used.
    /// </summary>
    public string ApiUrl { get; init; }

    /// <summary>
    /// Whether an API key is configured.
    /// </summary>
    public bool HasApiKey { get; init; }

    /// <summary>
    /// Whether a private key is configured (for sealed mode).
    /// </summary>
    public bool HasPrivateKey { get; init; }

    /// <summary>
    /// Whether the server is reachable at the resolved API URL.
    /// </summary>
    public bool ServerReachable { get; init; }

    /// <summary>
    /// Server version string if reachable, null otherwise.
    /// </summary>
    public string ServerVersion { get; init; }

    /// <summary>
    /// List of issues found during validation. Empty if IsValid is true.
    /// </summary>
    public List<string> Issues { get; init; } = new();
}
