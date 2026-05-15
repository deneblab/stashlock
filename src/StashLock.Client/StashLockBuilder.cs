using System;
using System.Threading;
using System.Threading.Tasks;
using Deneblab.StashLock.Client.Common.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deneblab.StashLock.Client;

/// <summary>
/// Fluent builder for configuring and opening a StashLock secrets store.
/// Use <see cref="StashLock.CreateClient"/> to get an instance.
/// </summary>
public class StashLockBuilder
{
    internal enum SourceMode
    {
        None,
        Box,
        DevFile,
        EncryptedFile,
        PlainFile
    }

    internal SourceMode Mode { get; private set; } = SourceMode.None;
    internal string Box { get; private set; }
    internal string Tag { get; private set; }
    internal string Version { get; private set; } = "00001";
    internal string PrivateKeyBase64 { get; private set; }
    internal string ApiUrl { get; private set; }
    internal string ApiKey { get; private set; }
    internal string FilePath { get; private set; }
    internal CacheOptions CacheOpts { get; private set; }
    internal TimeSpan? Timeout { get; private set; }
    internal ILoggerFactory LoggerFactory { get; private set; }

    internal StashLockBuilder()
    {
    }

    /// <summary>
    /// Configure from an ADO.NET-style connection string.
    /// Supported keys: Url, ApiKey, PrivateKey, Box, Tag.
    /// Accepts plain text or base64-encoded strings.
    /// Fluent API calls after this method override connection string values.
    /// </summary>
    public StashLockBuilder WithConnectionString(string connectionString)
    {
        var parsed = ConnectionStringParser.Parse(connectionString);
        ApplyConnectionString(parsed);
        return this;
    }

    /// <summary>
    /// Configure from the STASHLOCK_CONNECTION_STRING environment variable.
    /// </summary>
    public StashLockBuilder FromConnectionString()
    {
        var cs = ConnectionStringParser.FromEnvironment();
        var parsed = ConnectionStringParser.Parse(cs);
        ApplyConnectionString(parsed);
        return this;
    }

    private void ApplyConnectionString(System.Collections.Generic.Dictionary<string, string> parsed)
    {
        if (parsed.TryGetValue("Url", out var url))
            ApiUrl = url;
        if (parsed.TryGetValue("ApiKey", out var apiKey))
            ApiKey = apiKey;
        if (parsed.TryGetValue("PrivateKey", out var privateKey))
            PrivateKeyBase64 = privateKey;

        if (parsed.TryGetValue("Box", out var box) && parsed.TryGetValue("Tag", out var tag))
        {
            Mode = SourceMode.Box;
            Box = box;
            Tag = tag;
        }
        else if (parsed.TryGetValue("Box", out var boxOnly))
        {
            Box = boxOnly;
        }
        else if (parsed.TryGetValue("Tag", out var tagOnly))
        {
            Tag = tagOnly;
        }
    }

    /// <summary>
    /// Configure with a box and tag for sealed-box mode (X25519).
    /// </summary>
    public StashLockBuilder WithBox(string box, string tag, string version = "00001")
    {
        if (string.IsNullOrWhiteSpace(box))
            throw new ArgumentNullException(nameof(box));
        if (string.IsNullOrWhiteSpace(tag))
            throw new ArgumentNullException(nameof(tag));

        Mode = SourceMode.Box;
        Box = box;
        Tag = tag;
        Version = version ?? "00001";
        return this;
    }

    /// <summary>
    /// Configure to load from a plain JSON dev file.
    /// If no path is given, auto-discovers dev/secrets/secrets.json.
    /// </summary>
    public StashLockBuilder FromDevFile(string filePath = null)
    {
        Mode = SourceMode.DevFile;
        FilePath = filePath;
        return this;
    }

    /// <summary>
    /// Configure to load from a local encrypted file (SOPS or whole-file mode).
    /// </summary>
    public StashLockBuilder FromEncryptedFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentNullException(nameof(filePath));

        Mode = SourceMode.EncryptedFile;
        FilePath = filePath;
        return this;
    }

    /// <summary>
    /// Configure to load from a plain JSON file.
    /// </summary>
    public StashLockBuilder FromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentNullException(nameof(filePath));

        Mode = SourceMode.PlainFile;
        FilePath = filePath;
        return this;
    }

    /// <summary>
    /// Set the API URL for remote vault access.
    /// Falls back to STASHLOCK_API_URL environment variable or default.
    /// </summary>
    public StashLockBuilder WithApiUrl(string apiUrl)
    {
        ApiUrl = apiUrl;
        return this;
    }

    /// <summary>
    /// Set the API key for Bearer authentication.
    /// Falls back to STASHLOCK_API_KEY environment variable.
    /// </summary>
    public StashLockBuilder WithApiKey(string apiKey)
    {
        ApiKey = apiKey;
        return this;
    }

    /// <summary>
    /// Set the X25519 private key for sealed-box decryption.
    /// Falls back to STASHLOCK_PRIVATE_KEY environment variable.
    /// </summary>
    public StashLockBuilder WithPrivateKey(string privateKeyBase64)
    {
        PrivateKeyBase64 = privateKeyBase64;
        return this;
    }

    /// <summary>
    /// Enable encrypted cache for offline fallback.
    /// Cache is machine-bound and encrypted with a derived key.
    /// </summary>
    public StashLockBuilder WithCache(
        string cacheDir = null,
        TimeSpan? ttl = null,
        string machineId = null,
        CacheStrategy strategy = CacheStrategy.ServerFirst,
        TimeSpan? serverTimeout = null)
    {
        CacheOpts = new CacheOptions();
        if (cacheDir != null)
            CacheOpts.CacheDir = cacheDir;
        if (ttl.HasValue)
            CacheOpts.CacheTtl = ttl.Value;
        if (machineId != null)
            CacheOpts.MachineId = machineId;
        CacheOpts.Strategy = strategy;
        if (serverTimeout.HasValue)
            CacheOpts.ServerTimeout = serverTimeout.Value;
        return this;
    }

    /// <summary>
    /// Set the timeout for server requests.
    /// Default: 30 seconds (or 10 seconds when cache is enabled).
    /// </summary>
    public StashLockBuilder WithTimeout(TimeSpan timeout)
    {
        Timeout = timeout;
        return this;
    }

    /// <summary>
    /// Attach an <see cref="ILoggerFactory"/> so the client can emit structured logs
    /// (preferred — lets the client create per-category loggers). No logging occurs
    /// when unset (a no-op logger is used). Never logs secret values or key material.
    /// </summary>
    public StashLockBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        LoggerFactory = loggerFactory;
        return this;
    }

    /// <summary>
    /// Attach a single <see cref="ILogger"/>. Convenience wrapper over
    /// <see cref="WithLoggerFactory"/>; the same logger is used for every category.
    /// </summary>
    public StashLockBuilder WithLogger(ILogger logger)
    {
        LoggerFactory = logger == null ? null : new SingleLoggerFactory(logger);
        return this;
    }

    private sealed class SingleLoggerFactory : ILoggerFactory
    {
        private readonly ILogger _logger;
        public SingleLoggerFactory(ILogger logger) => _logger = logger;
        public ILogger CreateLogger(string categoryName) => _logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    /// <summary>
    /// Open the secrets store using the configured source.
    /// </summary>
    public async Task<ISecretsStore> OpenAsync(CancellationToken cancellationToken = default)
    {
        ApplyConnectionStringDefaults();

        var effectiveTimeout = ResolveTimeout();
        var log = (LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger("Deneblab.StashLock.Client");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        log.LogInformation(
            "StashLock open starting: mode={Mode} box={Box} tag={Tag} version={Version} timeout={TimeoutMs}ms",
            Mode, Box, Tag, Version, (int)effectiveTimeout.TotalMilliseconds);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(effectiveTimeout);
        var token = cts.Token;

        try
        {
            ISecretsStore result;
            switch (Mode)
            {
                case SourceMode.Box:
                    if (CacheOpts != null)
                        result = await SecretsStore.OpenRemoteSealedWithCacheAsync(
                            Box, Tag, Version, CacheOpts, PrivateKeyBase64, ApiUrl, ApiKey, cancellationToken);
                    else
                        result = await SecretsStore.OpenRemoteSealedAsync(
                            Box, Tag, Version, PrivateKeyBase64, ApiUrl, ApiKey, token);
                    break;

                case SourceMode.DevFile:
                    if (string.IsNullOrEmpty(FilePath))
                    {
                        var store = await SecretsStore.TryOpenDevFileAsync(token);
                        if (store == null)
                            throw new StashLockException("No dev secrets file found. Provide an explicit file path via FromDevFile(path).");
                        result = store;
                    }
                    else
                    {
                        result = await SecretsStore.OpenFileAsync(FilePath, token);
                    }
                    break;

                case SourceMode.EncryptedFile:
                    result = await SecretsStore.OpenEncryptedFileAsync(FilePath, PrivateKeyBase64, token);
                    break;

                case SourceMode.PlainFile:
                    result = await SecretsStore.OpenFileAsync(FilePath, token);
                    break;

                default:
                    throw new StashLockException(
                        "No source configured. Call WithConnectionString(), WithBox(), FromDevFile(), FromEncryptedFile(), or FromFile() before OpenAsync().");
            }

            log.LogInformation(
                "StashLock open succeeded: mode={Mode} box={Box} tag={Tag} version={Version} elapsed={ElapsedMs}ms",
                Mode, Box, Tag, Version, sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "StashLock open failed: mode={Mode} box={Box} tag={Tag} version={Version} error={ErrorType} elapsed={ElapsedMs}ms",
                Mode, Box, Tag, Version, ex.GetType().Name, sw.ElapsedMilliseconds);
            throw;
        }
    }

    /// <summary>
    /// Silently reads STASHLOCK_CONNECTION_STRING env var and applies values
    /// as defaults — only fills fields that are still null/unset.
    /// </summary>
    private void ApplyConnectionStringDefaults()
    {
        var cs = System.Environment.GetEnvironmentVariable("STASHLOCK_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(cs))
            return;

        System.Collections.Generic.Dictionary<string, string> parsed;
        try
        {
            parsed = ConnectionStringParser.Parse(cs);
        }
        catch
        {
            return; // invalid CS in env — silently skip, don't break existing flows
        }

        if (ApiUrl == null && parsed.TryGetValue("Url", out var url))
            ApiUrl = url;
        if (ApiKey == null && parsed.TryGetValue("ApiKey", out var apiKey))
            ApiKey = apiKey;
        if (PrivateKeyBase64 == null && parsed.TryGetValue("PrivateKey", out var privateKey))
            PrivateKeyBase64 = privateKey;

        // Only set Box/Tag/Mode if no source mode was explicitly configured
        if (Mode == SourceMode.None &&
            parsed.TryGetValue("Box", out var box) && parsed.TryGetValue("Tag", out var tag))
        {
            Mode = SourceMode.Box;
            Box = box;
            Tag = tag;
        }
    }

    private TimeSpan ResolveTimeout()
    {
        if (Timeout.HasValue)
            return Timeout.Value;
        if (CacheOpts != null)
            return CacheOpts.ServerTimeout;
        return TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Validate the configuration without opening a store.
    /// </summary>
    public async Task<ConfigValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        return await SecretsStore.ValidateConfigurationAsync(ApiUrl, ApiKey, PrivateKeyBase64, cancellationToken);
    }
}
