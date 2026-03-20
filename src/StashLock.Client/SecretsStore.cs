using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deneblab.StashLock.Client.Common;
using Deneblab.StashLock.Client.Common.Exceptions;
using Deneblab.StashLock.Client.Common.Simple;
using Deneblab.StashLock.Client.Modules.Web;

namespace Deneblab.StashLock.Client;

/// <summary>
///     Internal implementation of secrets storage.
///     Use <see cref="StashLock.CreateClient"/> fluent API to create instances.
/// </summary>
internal class SecretsStore : ISecretsStore
{
    private readonly Dictionary<string, string> _store;

    private SecretsStore(Dictionary<string, string> store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    ///     Gets all keys in the store.
    /// </summary>
    public IEnumerable<string> Keys => _store.Keys;

    /// <summary>
    ///     Gets the count of secrets.
    /// </summary>
    public int Count => _store.Count;

    /// <summary>
    ///     Gets a secret value by key synchronously.
    /// </summary>
    public string this[string key]
    {
        get
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));

            if (!_store.TryGetValue(key, out var value))
                throw new KeyNotFoundException($"Secret key not found: '{key}'. Available keys use colon-separated paths (e.g., 'Section:Key').");

            return value;
        }
    }

    /// <summary>
    ///     Tries to get a secret value by key without throwing.
    /// </summary>
    public bool TryGet(string key, out string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            value = null;
            return false;
        }

        return _store.TryGetValue(key, out value);
    }

    /// <summary>
    ///     Gets a section of secrets by prefix.
    /// </summary>
    public Dictionary<string, string> GetSectionAsDictionary(string sectionPrefix)
    {
        if (string.IsNullOrWhiteSpace(sectionPrefix))
            throw new ArgumentNullException(nameof(sectionPrefix));

        var dic = new Dictionary<string, string>();
        var validKey = sectionPrefix.EndsWith(':') ? sectionPrefix : $"{sectionPrefix}:";

        foreach (var kv in _store)
            if (kv.Key.StartsWith(validKey, StringComparison.OrdinalIgnoreCase))
            {
                var newKey = kv.Key.Substring(validKey.Length);
                dic[newKey] = kv.Value;
            }

        return dic;
    }

    /// <summary>
    ///     Gets a section and deserializes into a typed object.
    /// </summary>
    public T GetSection<T>(string sectionPrefix)
    {
        var section = GetSectionAsDictionary(sectionPrefix);
        var json = JsonSerializer.Serialize(section);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    /// <summary>
    ///     Creates a SecretsStore from a plain dictionary.
    /// </summary>
    internal static SecretsStore FromPlainDictionary(Dictionary<string, string> store)
    {
        return new SecretsStore(store);
    }

    /// <summary>
    ///     Returns a copy of the internal secrets dictionary. Internal use only (e.g., cache persistence).
    /// </summary>
    internal Dictionary<string, string> ToDictionary()
    {
        return new Dictionary<string, string>(_store);
    }

    #region Simplified Static Factory Methods

    private const string DEFAULT_API_URL = "";
    private const string ENV_API_URL = "STASHLOCK_API_URL";
    private const string ENV_API_KEY = "STASHLOCK_API_KEY";

    /// <summary>
    ///     Validates the configuration needed to connect to a remote vault.
    ///     Checks environment variables, API URL reachability, and optionally the private key format.
    ///     Use this as a pre-flight check before calling OpenRemoteAsync or OpenRemoteSealedAsync.
    /// </summary>
    /// <param name="apiUrl">Optional API URL override</param>
    /// <param name="apiKey">Optional API key override</param>
    /// <param name="privateKeyBase64">Optional private key to validate (for sealed mode)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A validation result with status and any issues found</returns>
    internal static async Task<ConfigValidationResult> ValidateConfigurationAsync(
        string apiUrl = null,
        string apiKey = null,
        string privateKeyBase64 = null,
        CancellationToken cancellationToken = default)
    {
        var issues = new System.Collections.Generic.List<string>();

        // Check API URL
        var resolvedApiUrl = apiUrl
                             ?? Environment.GetEnvironmentVariable(ENV_API_URL)
                             ?? DEFAULT_API_URL;

        // Check API key
        var resolvedApiKey = apiKey
                             ?? Environment.GetEnvironmentVariable(ENV_API_KEY);
        if (string.IsNullOrEmpty(resolvedApiKey))
            issues.Add($"No API key configured. Set {ENV_API_KEY} environment variable or pass apiKey parameter.");

        // Check private key format (if provided or in env)
        var resolvedPrivateKey = privateKeyBase64
                                 ?? Environment.GetEnvironmentVariable("STASHLOCK_PRIVATE_KEY");
        if (!string.IsNullOrEmpty(resolvedPrivateKey))
        {
            try
            {
                var bytes = Convert.FromBase64String(resolvedPrivateKey);
                if (bytes.Length != 32)
                    issues.Add($"Private key must be 32 bytes (got {bytes.Length}). Check your STASHLOCK_PRIVATE_KEY value.");
            }
            catch (FormatException)
            {
                issues.Add("Private key is not valid base64. Check your STASHLOCK_PRIVATE_KEY value.");
            }
        }

        // Check server connectivity
        bool serverReachable = false;
        string serverVersion = null;
        try
        {
            using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetAsync(resolvedApiUrl.TrimEnd('/') + "/", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                serverReachable = true;
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (body.StartsWith("OK"))
                    serverVersion = body;
            }
            else
            {
                issues.Add($"Server returned HTTP {(int)response.StatusCode} at {resolvedApiUrl}");
            }
        }
        catch (Exception ex)
        {
            issues.Add($"Cannot reach server at {resolvedApiUrl}: {ex.Message}");
        }

        return new ConfigValidationResult
        {
            IsValid = issues.Count == 0,
            ApiUrl = resolvedApiUrl,
            HasApiKey = !string.IsNullOrEmpty(resolvedApiKey),
            HasPrivateKey = !string.IsNullOrEmpty(resolvedPrivateKey),
            ServerReachable = serverReachable,
            ServerVersion = serverVersion,
            Issues = issues
        };
    }

    /// <summary>
    ///     Opens a secrets store from a plain JSON file (for development use only).
    ///     The file should contain a flat JSON object with key-value pairs.
    ///     Supports comments and trailing commas using SecretsFlattenedJsonConverter.
    /// </summary>
    /// <param name="filePath">Path to JSON secrets file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>ISecretsStore instance</returns>
    /// <exception cref="System.IO.FileNotFoundException">Thrown when file doesn't exist</exception>
    /// <exception cref="StashLockException">Thrown when file cannot be read or parsed</exception>
    internal static async Task<ISecretsStore> OpenFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentNullException(nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Secrets file not found: {filePath}", filePath);

        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken);
            var dictionary = DeserializeSecretsJson(json);

            return FromPlainDictionary(dictionary);
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            throw new StashLockException($"Failed to open secrets file '{filePath}': {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Auto-discovers and opens a development secrets file.
    ///     Looks for secrets.json in the DevDir/secrets directory.
    ///     Returns null if no dev environment is detected or file doesn't exist.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>ISecretsStore instance or null if not found</returns>
    internal static async Task<ISecretsStore> TryOpenDevFileAsync(CancellationToken cancellationToken = default)
    {
        var simpleEnv = SimpleEnv.Detect();

        if (simpleEnv.AppMode != SimpleAppMode.Dev && simpleEnv.AppMode != SimpleAppMode.Test)
            return null;

        if (string.IsNullOrEmpty(simpleEnv.AppRoot))
            return null;

        var devDir = Path.Combine(simpleEnv.AppRoot, "dev");

        var secretsDir = Path.Combine(devDir, "secrets");
        if (!Directory.Exists(secretsDir))
            return null;

        var secretsFile = Path.Combine(secretsDir, "secrets.json");
        if (!File.Exists(secretsFile))
            return null;

        try
        {
            return await OpenFileAsync(secretsFile, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    #region Sealed Box Factory Methods

    private const string ENV_PRIVATE_KEY = "STASHLOCK_PRIVATE_KEY";

    /// <summary>
    ///     Opens a secrets store from a remote vault containing Sealed Box encrypted secrets.
    ///     Fetches the encrypted blob from the vault, then decrypts using the X25519 private key.
    /// </summary>
    /// <param name="box">Box name (vault key segment 1)</param>
    /// <param name="tag">Tag name (vault key segment 2)</param>
    /// <param name="version">Version (vault key segment 3)</param>
    /// <param name="privateKeyBase64">Base64-encoded X25519 private key (32 bytes). Falls back to STASHLOCK_PRIVATE_KEY env var.</param>
    /// <param name="apiUrl">Optional API URL (defaults to STASHLOCK_API_URL env var or default URL)</param>
    /// <param name="apiKey">Optional API key (defaults to STASHLOCK_API_KEY env var)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>ISecretsStore instance with decrypted secrets</returns>
    internal static async Task<ISecretsStore> OpenRemoteSealedAsync(
        string box,
        string tag,
        string version = "00001",
        string privateKeyBase64 = null,
        string apiUrl = null,
        string apiKey = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(box)) throw new ArgumentNullException(nameof(box));
        if (string.IsNullOrWhiteSpace(tag)) throw new ArgumentNullException(nameof(tag));
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentNullException(nameof(version));

        var resolvedPrivateKey = privateKeyBase64
            ?? Environment.GetEnvironmentVariable(ENV_PRIVATE_KEY);

        if (string.IsNullOrWhiteSpace(resolvedPrivateKey))
            throw new Common.Exceptions.VaultConfigurationException(
                ENV_PRIVATE_KEY,
                $"No private key provided. Supply privateKeyBase64 or set {ENV_PRIVATE_KEY} environment variable.");

        var resolvedApiUrl = apiUrl
            ?? Environment.GetEnvironmentVariable(ENV_API_URL)
            ?? DEFAULT_API_URL;

        var resolvedApiKey = apiKey
            ?? Environment.GetEnvironmentVariable(ENV_API_KEY);

        byte[] privateKeyBytes;
        try
        {
            privateKeyBytes = Convert.FromBase64String(resolvedPrivateKey);
        }
        catch (FormatException ex)
        {
            throw new Common.Exceptions.VaultConfigurationException(
                nameof(privateKeyBase64),
                $"Invalid base64-encoded private key: {ex.Message}", ex);
        }

        // Construct the vault key path: box.tag.version (URL-safe base64 encoded)
        var webKey = $"{box}.{tag}.{version}";
        var webKeyBytes = System.Text.Encoding.UTF8.GetBytes(webKey);
        var webKeySafeUrl = Convert.ToBase64String(webKeyBytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        try
        {
            var httpClient = new Modules.Web.StashLockHttpClient(resolvedApiUrl, resolvedApiKey);
            var encryptedBase64 = await httpClient.GetAsync(webKeySafeUrl, cancellationToken);

            // Auto-detect: try SOPS/whole-file JSON first, fall back to raw ECIES blob
            var dictionary = Common.SopsDecryptor.DecryptAutoDetect(encryptedBase64, privateKeyBytes);
            return FromPlainDictionary(dictionary);
        }
        catch (Common.Exceptions.VaultNotFoundException)
        {
            throw;
        }
        catch (Common.Exceptions.DecryptionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not Common.Exceptions.StashLockException)
        {
            throw new Common.Exceptions.StashLockException($"Failed to open sealed remote secrets store: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Opens a secrets store from a remote vault with encrypted cache support.
    ///     Strategy is controlled by <see cref="CacheOptions.Strategy"/>:
    ///     ServerFirst (default) tries the server first, falls back to cache on failure.
    ///     CacheFirst tries the cache first, fetches from server only if cache is missing or expired.
    ///     Every successful server fetch updates the cache. Cache is machine-bound (not portable).
    /// </summary>
    internal static async Task<ISecretsStore> OpenRemoteSealedWithCacheAsync(
        string box,
        string tag,
        string version = "00001",
        CacheOptions cacheOptions = null,
        string privateKeyBase64 = null,
        string apiUrl = null,
        string apiKey = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(box)) throw new ArgumentNullException(nameof(box));
        if (string.IsNullOrWhiteSpace(tag)) throw new ArgumentNullException(nameof(tag));
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentNullException(nameof(version));

        // Resolve private key (needed for both server decryption and cache key derivation)
        var resolvedPrivateKey = privateKeyBase64
            ?? Environment.GetEnvironmentVariable(ENV_PRIVATE_KEY);

        if (string.IsNullOrWhiteSpace(resolvedPrivateKey))
            throw new VaultConfigurationException(
                ENV_PRIVATE_KEY,
                $"No private key provided. Supply privateKeyBase64 or set {ENV_PRIVATE_KEY} environment variable.");

        byte[] privateKeyBytes;
        try
        {
            privateKeyBytes = Convert.FromBase64String(resolvedPrivateKey);
        }
        catch (FormatException ex)
        {
            throw new VaultConfigurationException(
                nameof(privateKeyBase64),
                $"Invalid base64-encoded private key: {ex.Message}", ex);
        }

        // Resolve cache options
        var opts = cacheOptions ?? new CacheOptions();
        var cacheDir = opts.CacheDir;
        if (string.IsNullOrWhiteSpace(cacheDir))
        {
            var env = Common.Simple.SimpleEnv.Detect();
            cacheDir = Path.Combine(env.ConfigDir, "cache");
        }

        var cacheFilePath = SecretsCacheManager.GetCacheFilePath(cacheDir, box, tag, version);
        var cacheKey = SecretsCacheManager.DeriveCacheKey(privateKeyBytes, opts.MachineId);

        if (opts.Strategy == CacheStrategy.CacheFirst)
            return await OpenCacheFirstAsync(box, tag, version, privateKeyBase64, apiUrl, apiKey,
                opts, cacheFilePath, cacheKey, cancellationToken);

        return await OpenServerFirstAsync(box, tag, version, privateKeyBase64, apiUrl, apiKey,
            opts, cacheFilePath, cacheKey, cancellationToken);
    }

    private static async Task<ISecretsStore> OpenServerFirstAsync(
        string box, string tag, string version,
        string privateKeyBase64, string apiUrl, string apiKey,
        CacheOptions opts, string cacheFilePath, byte[] cacheKey,
        CancellationToken cancellationToken)
    {
        // Create a timeout-scoped token for the server call
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(opts.ServerTimeout);

        try
        {
            // 1. Try server first
            var store = await OpenRemoteSealedAsync(box, tag, version, privateKeyBase64, apiUrl, apiKey, cts.Token);

            // 2. On success, update cache
            try
            {
                var secrets = ((SecretsStore)store).ToDictionary();
                SecretsCacheManager.WriteCache(cacheFilePath, secrets, cacheKey, opts.CacheTtl);
            }
            catch
            {
                // Cache write failure should not break the main flow
            }
            finally
            {
                CryptographicOperations.ZeroMemory(cacheKey);
            }

            return store;
        }
        catch (DecryptionException)
        {
            // Wrong key = real error, never mask with stale cache
            CryptographicOperations.ZeroMemory(cacheKey);
            throw;
        }
        catch (Exception ex) when (ex is not StashLockException || ex is StashLockException { InnerException: not null })
        {
            // 3. Server failed — try cache fallback
            try
            {
                var cached = SecretsCacheManager.ReadCache(cacheFilePath, cacheKey);
                if (cached != null)
                    return FromPlainDictionary(cached);
            }
            catch
            {
                // Cache read failure — fall through to re-throw
            }
            finally
            {
                CryptographicOperations.ZeroMemory(cacheKey);
            }

            // No valid cache — re-throw original exception
            throw;
        }
    }

    private static async Task<ISecretsStore> OpenCacheFirstAsync(
        string box, string tag, string version,
        string privateKeyBase64, string apiUrl, string apiKey,
        CacheOptions opts, string cacheFilePath, byte[] cacheKey,
        CancellationToken cancellationToken)
    {
        // 1. Try cache first
        try
        {
            var cached = SecretsCacheManager.ReadCache(cacheFilePath, cacheKey);
            if (cached != null)
            {
                CryptographicOperations.ZeroMemory(cacheKey);
                return FromPlainDictionary(cached);
            }
        }
        catch
        {
            // Cache read failure — fall through to server
        }

        // 2. Cache miss or expired — fetch from server with timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(opts.ServerTimeout);

        try
        {
            var store = await OpenRemoteSealedAsync(box, tag, version, privateKeyBase64, apiUrl, apiKey, cts.Token);

            // 3. On success, update cache
            try
            {
                var secrets = ((SecretsStore)store).ToDictionary();
                SecretsCacheManager.WriteCache(cacheFilePath, secrets, cacheKey, opts.CacheTtl);
            }
            catch
            {
                // Cache write failure should not break the main flow
            }
            finally
            {
                CryptographicOperations.ZeroMemory(cacheKey);
            }

            return store;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(cacheKey);
            throw;
        }
    }

    /// <summary>
    ///     Opens a secrets store from a local encrypted file (SOPS or whole-file mode).
    ///     Auto-detects the encryption mode from file content.
    /// </summary>
    /// <param name="filePath">Path to the stashlock.enc.*.json encrypted file</param>
    /// <param name="privateKeyBase64">Base64-encoded X25519 private key (32 bytes). Falls back to STASHLOCK_PRIVATE_KEY env var.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>ISecretsStore instance with decrypted secrets</returns>
    internal static async Task<ISecretsStore> OpenEncryptedFileAsync(
        string filePath,
        string privateKeyBase64 = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentNullException(nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Encrypted file not found: {filePath}", filePath);

        var resolvedPrivateKey = privateKeyBase64
            ?? Environment.GetEnvironmentVariable(ENV_PRIVATE_KEY);

        if (string.IsNullOrWhiteSpace(resolvedPrivateKey))
            throw new VaultConfigurationException(
                ENV_PRIVATE_KEY,
                $"No private key provided. Supply privateKeyBase64 or set {ENV_PRIVATE_KEY} environment variable.");

        byte[] privateKeyBytes;
        try
        {
            privateKeyBytes = Convert.FromBase64String(resolvedPrivateKey);
        }
        catch (FormatException ex)
        {
            throw new VaultConfigurationException(
                nameof(privateKeyBase64),
                $"Invalid base64-encoded private key: {ex.Message}", ex);
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken);
            var dictionary = SopsDecryptor.DecryptAutoDetect(json, privateKeyBytes);
            return FromPlainDictionary(dictionary);
        }
        catch (DecryptionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not StashLockException)
        {
            throw new StashLockException($"Failed to open encrypted file '{filePath}': {ex.Message}", ex);
        }
    }

    #endregion

    private static Dictionary<string, string> DeserializeSecretsJson(string json)
    {
        var options = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new SecretsFlattenedJsonConverter() }
        };

        var model = JsonSerializer.Deserialize<SecretsModel>(json, options);
        if (model == null || model.Values == null) throw new StashLockException("Failed to deserialize secrets JSON");

        return model.Values.ToDictionary(x => x.Key, y => y.Value?.ToString() ?? string.Empty);
    }

    #endregion
}