using System;

namespace Deneblab.StashLock.Client;

/// <summary>
/// Controls the order in which the client tries cache vs server.
/// </summary>
public enum CacheStrategy
{
    /// <summary>
    /// Try the server first; fall back to cache on failure. This is the default.
    /// </summary>
    ServerFirst = 0,

    /// <summary>
    /// Try the cache first; fetch from server only if cache is missing or expired.
    /// Best for edge deployments or environments with poor connectivity.
    /// </summary>
    CacheFirst = 1
}

/// <summary>
/// Options for client-side encrypted cache. Enables offline fallback when the server is unreachable.
/// Cache is encrypted with a machine-bound key derived from the private key and machine identity.
/// </summary>
public class CacheOptions
{
    /// <summary>
    /// Cache directory path. Defaults to SimpleEnv.ConfigDir + "/cache/".
    /// </summary>
    public string CacheDir { get; set; }

    /// <summary>
    /// Time-to-live for cached secrets. Expired cache is not used as fallback.
    /// Default: 1 hour.
    /// </summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Optional machine identity override for cache key derivation.
    /// Defaults to Environment.MachineName.
    /// Set explicitly in Docker/Kubernetes where hostname changes on each restart.
    /// </summary>
    public string MachineId { get; set; }

    /// <summary>
    /// Controls whether to try the cache or server first.
    /// Default: ServerFirst (existing behavior).
    /// </summary>
    public CacheStrategy Strategy { get; set; } = CacheStrategy.ServerFirst;

    /// <summary>
    /// Timeout for server requests when cache is enabled.
    /// A shorter timeout allows faster failover to cache.
    /// Default: 10 seconds.
    /// </summary>
    public TimeSpan ServerTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
