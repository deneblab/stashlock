using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Deneblab.StashLock.Cli.Modules.Publish;

internal sealed class StashLockApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public StashLockApiClient(string baseUrl, string? apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient();
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<PublishResult> PublishAsync(string vaultKey, string content, int? expiresIn = null,
        CancellationToken ct = default)
    {
        try
        {
            var encoded = Base64UrlEncode(vaultKey);
            var url = $"{_baseUrl}/boxes/{encoded}";
            if (expiresIn.HasValue)
                url += $"?expiresIn={expiresIn.Value}";

            using var body = new StringContent(content, Encoding.UTF8, "text/plain");
            using var response = await _http.PostAsync(url, body, ct);

            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                var apiResponse = JsonSerializer.Deserialize<PublishApiResponse>(responseBody);
                return PublishResult.Success(apiResponse?.Key, apiResponse?.Message);
            }

            return PublishResult.Failure((int)response.StatusCode, responseBody);
        }
        catch (HttpRequestException ex)
        {
            return PublishResult.Failure(0, $"HTTP error: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            return PublishResult.Failure(0, $"Request timed out: {ex.Message}");
        }
    }

    private static string Base64UrlEncode(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .Replace("=", "");
    }

    public void Dispose() => _http.Dispose();
}
