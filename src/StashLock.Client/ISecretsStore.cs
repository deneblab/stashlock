using System.Collections.Generic;

namespace Deneblab.StashLock.Client;

/// <summary>
/// Interface for secret storage operations.
/// </summary>
public interface ISecretsStore
{
    /// <summary>
    /// Gets a secret value by key.
    /// </summary>
    /// <param name="key">The secret key</param>
    /// <returns>The secret value</returns>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">Thrown when key is not found</exception>
    string this[string key] { get; }

    /// <summary>
    /// Tries to get a secret value by key without throwing.
    /// </summary>
    /// <param name="key">The secret key</param>
    /// <param name="value">The secret value if found</param>
    /// <returns>True if key exists, false otherwise</returns>
    bool TryGet(string key, out string value);

    /// <summary>
    /// Gets a section of secrets by prefix.
    /// </summary>
    /// <param name="sectionPrefix">The section prefix (with or without trailing ':')</param>
    /// <returns>Dictionary of secrets in the section</returns>
    Dictionary<string, string> GetSectionAsDictionary(string sectionPrefix);

    /// <summary>
    /// Gets a section of secrets and deserializes into a typed object.
    /// Keys are matched to properties using case-insensitive comparison.
    /// </summary>
    /// <typeparam name="T">Target type to deserialize into</typeparam>
    /// <param name="sectionPrefix">The section prefix (with or without trailing ':')</param>
    /// <returns>Deserialized object of type T</returns>
    T GetSection<T>(string sectionPrefix);
}
