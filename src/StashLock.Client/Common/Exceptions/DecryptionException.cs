using System;

namespace Deneblab.StashLock.Client.Common.Exceptions;

/// <summary>
/// Exception thrown when decryption of vault data fails.
/// </summary>
public class DecryptionException : StashLockException
{
    /// <summary>Creates the exception with a default message.</summary>
    public DecryptionException()
        : base("Failed to decrypt vault data. Possible causes: (1) incorrect password or private key, (2) corrupted data, (3) a file encoded for another box.tag.version.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public DecryptionException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the error that caused it.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying error.</param>
    public DecryptionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
