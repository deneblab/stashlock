using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Deneblab.StashLock.Client.Common.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger _log;
    private bool _disposed;

    /// <summary>Creates a client for the StashLock server.</summary>
    /// <param name="baseUrl">Server URL, e.g. <c>https://vault.example.com</c>.</param>
    /// <param name="apiKey">API key sent as Bearer token.</param>
    /// <param name="logger">Optional logger for request timing and errors (never secrets).</param>
    public StashLockHttpClient(string baseUrl, string apiKey = null, ILogger logger = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new VaultConfigurationException("ApiUrl",
                "No server URL. Call WithApiUrl(), set STASHLOCK_API_URL, or add Url=... to the connection string.");

        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _log = logger ?? NullLogger.Instance;
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

        _log.LogDebug("HTTP GET {Path}", $"/boxes/{key}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await SharedHttpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _log.LogWarning("HTTP GET {Path} -> 404 NotFound ({ElapsedMs}ms)", $"/boxes/{key}", sw.ElapsedMilliseconds);
                throw new VaultNotFoundException(key);
            }

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                _log.LogWarning("HTTP GET {Path} -> {StatusCode} ({ElapsedMs}ms)", $"/boxes/{key}", (int)response.StatusCode, sw.ElapsedMilliseconds);
                var described = DescribeKey(key);
                throw new VaultAccessDeniedException(described, response.StatusCode,
                    response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        ? $"The server rejected the API key (401) for '{described}'. Check STASHLOCK_API_KEY: the key may be wrong, revoked or expired."
                        : $"The API key is not allowed to read '{described}' (403). Use an API key created for this box.tag with Read access; admin keys cannot read secrets.");
            }

            response.EnsureSuccessStatusCode();

            _log.LogInformation("HTTP GET {Path} -> {StatusCode} ({ElapsedMs}ms)",
                $"/boxes/{key}", (int)response.StatusCode, sw.ElapsedMilliseconds);
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (VaultNotFoundException)
        {
            throw;
        }
        catch (StashLockException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _log.LogError("HTTP GET {Path} failed: connection error ({ElapsedMs}ms)", $"/boxes/{key}", sw.ElapsedMilliseconds);
            throw new StashLockException($"Failed to retrieve vault entry for key '{key}': {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _log.LogError("HTTP GET {Path} failed: timeout ({ElapsedMs}ms)", $"/boxes/{key}", sw.ElapsedMilliseconds);
            throw new StashLockException($"Request timeout while retrieving vault entry for key '{key}'", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        // Note: We don't dispose SharedHttpClient as it's static and shared
        _disposed = true;
    }

    // Decodes a base64url key to "box.tag.version" for readable messages.
    private static string DescribeKey(string key)
    {
        try
        {
            var b64 = key.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (FormatException)
        {
            return key;
        }
    }
}
