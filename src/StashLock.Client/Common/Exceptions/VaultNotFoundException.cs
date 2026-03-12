using System;

namespace Deneblab.StashLock.Client.Common.Exceptions;

/// <summary>
/// Exception thrown when a vault secret cannot be found.
/// </summary>
public class VaultNotFoundException : StashLockException
{
    public string VaultKey { get; }

    public VaultNotFoundException(string vaultKey)
        : base($"Vault entry not found for key: '{vaultKey}'")
    {
        VaultKey = vaultKey;
    }

    public VaultNotFoundException(string vaultKey, string message) : base(message)
    {
        VaultKey = vaultKey;
    }

    public VaultNotFoundException(string vaultKey, string message, Exception innerException)
        : base(message, innerException)
    {
        VaultKey = vaultKey;
    }
}
