using System;

namespace Deneblab.StashLock.Client.Common.Exceptions;

/// <summary>
/// Exception thrown when decryption of vault data fails.
/// </summary>
public class DecryptionException : StashLockException
{
    public DecryptionException()
        : base("Failed to decrypt vault data. Possible causes: (1) incorrect password or private key, (2) corrupted data, (3) encryption mode mismatch (SOPS vs whole-file).")
    {
    }

    public DecryptionException(string message) : base(message)
    {
    }

    public DecryptionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
