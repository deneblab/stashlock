using System;

namespace Deneblab.StashLock.Client.Common.Exceptions;

/// <summary>
/// Base exception for all StashLock.Client errors.
/// </summary>
public class StashLockException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public StashLockException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public StashLockException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the error that caused it.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying error.</param>
    public StashLockException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
