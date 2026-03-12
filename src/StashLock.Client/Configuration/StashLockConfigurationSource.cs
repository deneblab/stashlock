using Microsoft.Extensions.Configuration;

namespace Deneblab.StashLock.Client.Configuration;

/// <summary>
/// Configuration source for StashLock secrets.
/// Supports remote (StashLock.Server), local encrypted file, and plain dev file modes.
/// </summary>
internal class StashLockConfigurationSource : IConfigurationSource
{
    /// <summary>Box name (vault key segment 1). Remote mode only.</summary>
    public string Box { get; set; }

    /// <summary>Tag name (vault key segment 2). Remote mode only.</summary>
    public string Tag { get; set; }

    /// <summary>Version (vault key segment 3). Remote mode only.</summary>
    public string Version { get; set; }

    /// <summary>Base64-encoded X25519 private key. Falls back to STASHLOCK_PRIVATE_KEY env var.</summary>
    public string PrivateKeyBase64 { get; set; }

    /// <summary>API URL. Falls back to STASHLOCK_API_URL env var or default.</summary>
    public string ApiUrl { get; set; }

    /// <summary>API key for Bearer auth. Falls back to STASHLOCK_API_KEY env var.</summary>
    public string ApiKey { get; set; }

    /// <summary>Path to encrypted or plain secrets file. File mode only.</summary>
    public string FilePath { get; set; }

    /// <summary>Cache options for offline fallback. Remote mode only. Null = no caching.</summary>
    public CacheOptions CacheOptions { get; set; }

    /// <summary>Source mode.</summary>
    public StashLockSourceMode Mode { get; set; }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new StashLockConfigurationProvider(this);
    }
}

internal enum StashLockSourceMode
{
    Remote,
    EncryptedFile,
    DevFile
}
