using System;

namespace Deneblab.StashLock.Client.Common.Exceptions;

/// <summary>
/// Base exception for all StashLock.Client errors.
/// </summary>
public class StashLockException : Exception
{
    public StashLockException()
    {
    }

    public StashLockException(string message) : base(message)
    {
    }

    public StashLockException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
