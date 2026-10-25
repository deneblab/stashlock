using System;

namespace Deneblab.StashLock.Client.Common.Exceptions;

/// <summary>
/// Exception thrown when vault configuration is invalid or missing.
/// </summary>
public class VaultConfigurationException : StashLockException
{
    /// <summary>The setting that is missing or invalid (e.g. <c>ApiUrl</c>, <c>STASHLOCK_PRIVATE_KEY</c>).</summary>
    public string ConfigurationKey { get; }

    /// <summary>Creates the exception for a setting.</summary>
    /// <param name="configurationKey">The setting that is missing or invalid.</param>
    /// <param name="message">What is wrong and how to fix it.</param>
    public VaultConfigurationException(string configurationKey, string message)
        : base(message)
    {
        ConfigurationKey = configurationKey;
    }

    /// <summary>Creates the exception for a setting, with the error that caused it.</summary>
    /// <param name="configurationKey">The setting that is missing or invalid.</param>
    /// <param name="message">What is wrong and how to fix it.</param>
    /// <param name="innerException">The underlying error.</param>
    public VaultConfigurationException(string configurationKey, string message, Exception innerException)
        : base(message, innerException)
    {
        ConfigurationKey = configurationKey;
    }
}
