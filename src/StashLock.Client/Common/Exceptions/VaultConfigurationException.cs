using System;

namespace Deneblab.StashLock.Client.Common.Exceptions;

/// <summary>
/// Exception thrown when vault configuration is invalid or missing.
/// </summary>
public class VaultConfigurationException : StashLockException
{
    public string ConfigurationKey { get; }

    public VaultConfigurationException(string configurationKey, string message)
        : base(message)
    {
        ConfigurationKey = configurationKey;
    }

    public VaultConfigurationException(string configurationKey, string message, Exception innerException)
        : base(message, innerException)
    {
        ConfigurationKey = configurationKey;
    }
}
