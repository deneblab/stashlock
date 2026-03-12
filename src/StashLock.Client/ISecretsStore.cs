using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Deneblab.StashLock.Client;

/// <summary>
/// Interface for secret storage operations.
/// </summary>
public interface ISecretsStore
{
    /// <summary>
    /// Gets a secret value by key synchronously.
    /// </summary>
    /// <param name="key">The secret key</param>
    /// <returns>The secret value</returns>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">Thrown when key is not found</exception>
    string this[string key] { get; }

    /// <summary>
    /// Gets a secret value by key asynchronously.
    /// </summary>
    /// <param name="key">The secret key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The secret value</returns>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">Thrown when key is not found</exception>
    Task<string> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tries to get a secret value by key without throwing.
    /// </summary>
    /// <param name="key">The secret key</param>
    /// <param name="value">The secret value if found</param>
    /// <returns>True if key exists, false otherwise</returns>
    bool TryGet(string key, out string value);

    /// <summary>
    /// Gets a section of secrets by prefix asynchronously.
    /// </summary>
    /// <param name="sectionPrefix">The section prefix (with or without trailing ':')</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of secrets in the section</returns>
    Task<Dictionary<string, string>> GetSectionAsDictionaryAsync(string sectionPrefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a section of secrets and deserializes into a typed object.
    /// Keys are matched to properties using case-insensitive comparison.
    /// </summary>
    /// <typeparam name="T">Target type to deserialize into</typeparam>
    /// <param name="sectionPrefix">The section prefix (with or without trailing ':')</param>
    /// <returns>Deserialized object of type T</returns>
    T GetSection<T>(string sectionPrefix);
}
