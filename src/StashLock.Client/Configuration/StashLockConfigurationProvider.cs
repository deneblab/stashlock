using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deneblab.StashLock.Client.Common;
using Deneblab.StashLock.Client.Common.Exceptions;
using Deneblab.StashLock.Client.Common.Simple;
using Deneblab.StashLock.Client.Modules.Web;
using Microsoft.Extensions.Configuration;

namespace Deneblab.StashLock.Client.Configuration;

/// <summary>
/// Configuration provider that loads secrets from StashLock sources
/// (remote server, local encrypted file, or plain dev file)
/// and populates IConfiguration data.
/// </summary>
internal class StashLockConfigurationProvider : ConfigurationProvider
{
    private const string ENV_PRIVATE_KEY = "STASHLOCK_PRIVATE_KEY";
    private const string ENV_API_URL = "STASHLOCK_API_URL";
    private const string ENV_API_KEY = "STASHLOCK_API_KEY";
    private const string DEFAULT_API_URL = "";

    private readonly StashLockConfigurationSource _source;

    public StashLockConfigurationProvider(StashLockConfigurationSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public override void Load()
    {
        Dictionary<string, string> secrets;

        switch (_source.Mode)
        {
            case StashLockSourceMode.Remote:
                secrets = LoadRemote();
                break;
            case StashLockSourceMode.EncryptedFile:
                secrets = LoadEncryptedFile();
                break;
            case StashLockSourceMode.DevFile:
                secrets = LoadDevFile();
                break;
            default:
                throw new StashLockException($"Unknown source mode: {_source.Mode}");
        }

        Data = new Dictionary<string, string>(secrets, StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, string> LoadRemote()
    {
        var privateKeyBytes = ResolvePrivateKey();
        var apiUrl = _source.ApiUrl
                     ?? Environment.GetEnvironmentVariable(ENV_API_URL)
                     ?? DEFAULT_API_URL;
        var apiKey = _source.ApiKey
                     ?? Environment.GetEnvironmentVariable(ENV_API_KEY);

        var webKey = $"{_source.Box}.{_source.Tag}.{_source.Version}";
        var webKeyBytes = Encoding.UTF8.GetBytes(webKey);
        var webKeySafeUrl = Convert.ToBase64String(webKeyBytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        // Resolve cache (if configured)
        byte[] cacheKey = null;
        string cacheFilePath = null;
        var cacheOpts = _source.CacheOptions;
        if (cacheOpts != null)
        {
            var cacheDir = cacheOpts.CacheDir;
            if (string.IsNullOrWhiteSpace(cacheDir))
            {
                var env = SimpleEnv.Detect();
                cacheDir = Path.Combine(env.ConfigDir, "cache");
            }
            cacheFilePath = SecretsCacheManager.GetCacheFilePath(cacheDir, _source.Box, _source.Tag, _source.Version);
            cacheKey = SecretsCacheManager.DeriveCacheKey(privateKeyBytes, cacheOpts.MachineId);
        }

        // CacheFirst: try cache before server
        if (cacheOpts != null && cacheOpts.Strategy == CacheStrategy.CacheFirst && cacheKey != null)
        {
            try
            {
                var cached = SecretsCacheManager.ReadCache(cacheFilePath, cacheKey);
                if (cached != null)
                {
                    CryptographicOperations.ZeroMemory(cacheKey);
                    return cached;
                }
            }
            catch { }
            // Cache miss — fall through to server fetch
        }

        // Resolve server timeout
        var serverTimeout = cacheOpts?.ServerTimeout ?? TimeSpan.FromSeconds(30);

        try
        {
            var httpClient = new StashLockHttpClient(apiUrl, apiKey);
            using var cts = new CancellationTokenSource(serverTimeout);
            var encryptedData = Task.Run(() => httpClient.GetAsync(webKeySafeUrl, cts.Token)).GetAwaiter().GetResult();

            var secrets = SopsDecryptor.DecryptAutoDetect(encryptedData, privateKeyBytes);

            // Update cache on success
            if (cacheKey != null)
            {
                try
                {
                    SecretsCacheManager.WriteCache(cacheFilePath, secrets, cacheKey, cacheOpts.CacheTtl);
                }
                catch { }
            }

            return secrets;
        }
        catch (DecryptionException)
        {
            throw;
        }
        catch (Exception ex) when (cacheKey != null)
        {
            // Server failed — try cache fallback
            try
            {
                var cached = SecretsCacheManager.ReadCache(cacheFilePath, cacheKey);
                if (cached != null)
                    return cached;
            }
            catch { }

            // No valid cache
            if (ex is StashLockException) throw;
            throw new StashLockException($"Failed to open remote secrets store: {ex.Message}", ex);
        }
        finally
        {
            if (cacheKey != null)
                CryptographicOperations.ZeroMemory(cacheKey);
        }
    }

    private Dictionary<string, string> LoadEncryptedFile()
    {
        if (!File.Exists(_source.FilePath))
            throw new FileNotFoundException($"Encrypted secrets file not found: {_source.FilePath}", _source.FilePath);

        var privateKeyBytes = ResolvePrivateKey();
        var json = File.ReadAllText(_source.FilePath);

        return SopsDecryptor.DecryptAutoDetect(json, privateKeyBytes);
    }

    private Dictionary<string, string> LoadDevFile()
    {
        if (!File.Exists(_source.FilePath))
            throw new FileNotFoundException($"Dev secrets file not found: {_source.FilePath}", _source.FilePath);

        var json = File.ReadAllText(_source.FilePath);
        return DeserializePlainJson(json);
    }

    private byte[] ResolvePrivateKey()
    {
        var privateKeyBase64 = _source.PrivateKeyBase64
                               ?? Environment.GetEnvironmentVariable(ENV_PRIVATE_KEY);

        if (string.IsNullOrWhiteSpace(privateKeyBase64))
            throw new VaultConfigurationException(
                ENV_PRIVATE_KEY,
                $"No private key provided. Supply privateKeyBase64 or set {ENV_PRIVATE_KEY} environment variable.");

        try
        {
            return Convert.FromBase64String(privateKeyBase64);
        }
        catch (FormatException ex)
        {
            throw new VaultConfigurationException(
                "privateKeyBase64",
                $"Invalid base64-encoded private key: {ex.Message}", ex);
        }
    }

    private static Dictionary<string, string> DeserializePlainJson(string json)
    {
        var options = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var result = new Dictionary<string, string>();
        FlattenElement(doc.RootElement, "", result);
        return result;
    }

    private static void FlattenElement(JsonElement element, string prefix, Dictionary<string, string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}:{prop.Name}";
                    FlattenElement(prop.Value, key, result);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var key = $"{prefix}:{index}";
                    FlattenElement(item, key, result);
                    index++;
                }
                break;

            default:
                result[prefix] = element.ToString();
                break;
        }
    }
}
