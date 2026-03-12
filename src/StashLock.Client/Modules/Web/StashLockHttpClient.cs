using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Deneblab.StashLock.Client.Common.Exceptions;

namespace Deneblab.StashLock.Client.Modules.Web;

/// <summary>
/// Modern async-first HTTP client for StashLock vault operations.
/// Uses a shared static HttpClient for proper resource management.
/// </summary>
public class StashLockHttpClient : IDisposable
{
    private static readonly HttpClient SharedHttpClient = new HttpClient
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private bool _disposed;

    public StashLockHttpClient(string baseUrl, string apiKey = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new VaultConfigurationException("BaseUrl", "API base URL cannot be null or empty");

        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
    }

    /// <summary>
    /// Gets a vault entry by key asynchronously.
    /// </summary>
    /// <param name="key">The vault key (Base64 URL-encoded)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The encrypted vault data</returns>
    /// <exception cref="VaultNotFoundException">Thrown when the vault entry is not found</exception>
    /// <exception cref="StashLockException">Thrown for other vault errors</exception>
    public async Task<string> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentNullException(nameof(key));

        var url = $"{_baseUrl}/boxes/{key}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
        }
        request.Headers.Add("Accept", "text/plain");

        try
        {
            var response = await SharedHttpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new VaultNotFoundException(key);
            }

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (VaultNotFoundException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new StashLockException($"Failed to retrieve vault entry for key '{key}': {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StashLockException($"Request timeout while retrieving vault entry for key '{key}'", ex);
        }
    }

    /// <summary>
    /// Creates or updates a vault entry asynchronously.
    /// </summary>
    /// <param name="key">The vault key (Base64 URL-encoded)</param>
    /// <param name="encryptedData">The encrypted data to store</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <exception cref="StashLockException">Thrown when the operation fails</exception>
    public async Task SetAsync(string key, string encryptedData, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentNullException(nameof(key));
        if (string.IsNullOrWhiteSpace(encryptedData))
            throw new ArgumentNullException(nameof(encryptedData));

        var url = $"{_baseUrl}/boxes/{key}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
        }

        request.Content = new StringContent(encryptedData, System.Text.Encoding.UTF8, "text/plain");

        try
        {
            var response = await SharedHttpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            throw new StashLockException($"Failed to set vault entry for key '{key}': {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StashLockException($"Request timeout while setting vault entry for key '{key}'", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Note: We don't dispose SharedHttpClient as it's static and shared
        _disposed = true;
    }
}
