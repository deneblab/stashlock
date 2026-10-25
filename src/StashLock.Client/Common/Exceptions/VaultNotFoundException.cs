using System;

namespace Deneblab.StashLock.Client.Common.Exceptions;

/// <summary>
/// Exception thrown when a vault secret cannot be found.
/// </summary>
public class VaultNotFoundException : StashLockException
{
    /// <summary>The requested entry (base64url key as sent to the server).</summary>
    public string VaultKey { get; }

    /// <summary>Creates the exception for an entry.</summary>
    /// <param name="vaultKey">The requested entry.</param>
    public VaultNotFoundException(string vaultKey)
        : base($"Vault entry not found for key: '{vaultKey}'")
    {
        VaultKey = vaultKey;
    }

    /// <summary>Creates the exception for an entry with a message.</summary>
    /// <param name="vaultKey">The requested entry.</param>
    /// <param name="message">What went wrong.</param>
    public VaultNotFoundException(string vaultKey, string message) : base(message)
    {
        VaultKey = vaultKey;
    }

    /// <summary>Creates the exception for an entry with a message and the error that caused it.</summary>
    /// <param name="vaultKey">The requested entry.</param>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying error.</param>
    public VaultNotFoundException(string vaultKey, string message, Exception innerException)
        : base(message, innerException)
    {
        VaultKey = vaultKey;
    }
}
