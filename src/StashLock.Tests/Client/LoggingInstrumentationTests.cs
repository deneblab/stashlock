using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Deneblab.StashLock.Client.Common;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Deneblab.StashLock.Tests.Client;

/// <summary>
/// SL-059: verifies cache instrumentation emits events AND never leaks secret
/// material into log output (security regression guard).
/// </summary>
public class LoggingInstrumentationTests : IDisposable
{
    private readonly List<string> _logs = new();
    private readonly List<string> _temp = new();

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> _sink;
        public CapturingLogger(List<string> sink) => _sink = sink;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => _sink.Add(logLevel + ": " + formatter(state, exception));
    }

    public void Dispose()
    {
        foreach (var f in _temp)
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void Cache_roundtrip_emits_events_and_never_leaks_secret_material()
    {
        var logger = new CapturingLogger(_logs);

        const string secretValue = "SUPER_SECRET_VALUE_DO_NOT_LOG_42";
        const string secretKeyName = "Database:Password";
        var privateKeyBytes = RandomNumberGenerator.GetBytes(32);
        var cacheKey = SecretsCacheManager.DeriveCacheKey(privateKeyBytes, "test-machine");
        var cacheKeyB64 = Convert.ToBase64String(cacheKey);
        var privKeyB64 = Convert.ToBase64String(privateKeyBytes);

        var path = Path.Combine(Path.GetTempPath(), $"sl-log-test-{Guid.NewGuid():N}.bin");
        _temp.Add(path);

        var secrets = new Dictionary<string, string> { [secretKeyName] = secretValue };

        SecretsCacheManager.WriteCache(path, secrets, cacheKey, TimeSpan.FromHours(1), logger);
        var read = SecretsCacheManager.ReadCache(path, cacheKey, ignoreTtl: false, logger: logger);

        // Behavior unchanged by instrumentation
        Assert.NotNull(read);
        Assert.Equal(secretValue, read![secretKeyName]);

        var all = string.Join("\n", _logs);

        // Events emitted
        Assert.Contains(_logs, l => l.Contains("Cache write"));
        Assert.Contains(_logs, l => l.Contains("Cache hit"));

        // SECURITY GUARD — no secret material in any captured log line
        Assert.DoesNotContain(secretValue, all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(cacheKeyB64, all, StringComparison.Ordinal);
        Assert.DoesNotContain(privKeyB64, all, StringComparison.Ordinal);
    }

    [Fact]
    public void Cache_miss_when_absent_is_logged_without_secrets()
    {
        var logger = new CapturingLogger(_logs);
        var cacheKey = SecretsCacheManager.DeriveCacheKey(RandomNumberGenerator.GetBytes(32));
        var path = Path.Combine(Path.GetTempPath(), $"sl-absent-{Guid.NewGuid():N}.bin");

        var read = SecretsCacheManager.ReadCache(path, cacheKey, logger: logger);

        Assert.Null(read);
        Assert.Contains(_logs, l => l.Contains("Cache miss") && l.Contains("not present"));
        Assert.DoesNotContain(Convert.ToBase64String(cacheKey), string.Join("\n", _logs), StringComparison.Ordinal);
    }
}
