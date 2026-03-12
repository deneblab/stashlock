using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Deneblab.StashLock.Server.Common.Misc;
using Deneblab.StashLock.Server.Common.Simple;
using Deneblab.StashLock.Server.Controllers;
using Deneblab.StashLock.Server.Data.Entities;
using Deneblab.StashLock.Server.Exceptions;
using Deneblab.StashLock.Server.Models;
using Deneblab.StashLock.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using Xunit;

namespace Deneblab.StashLock.Tests.Controllers;

public class BoxesControllerTests : IDisposable
{
    private readonly BoxesController _controller;
    private readonly FakeStorageService _storageService;
    private readonly FakeAuditLogService _auditLogService;

    public BoxesControllerTests()
    {
        _storageService = new FakeStorageService();
        _auditLogService = new FakeAuditLogService();
        var logger = LoggerFactory.Create(builder => builder.AddNLog())
            .CreateLogger<BoxesController>();
        var env = new SimpleEnvResult(
            SimpleAppMode.Test,
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath()
        );
        var version = new SimpleVersionInfo(
            "TestProduct", "1.0.0-test", 0, "test",
            DateTime.UtcNow, "test", "abc123", 0,
            new Dictionary<string, string>()
        );
        var registry = new Registry(env, version);

        _controller = new BoxesController(_storageService, logger, registry, _auditLogService);
    }

    public void Dispose()
    {
    }

    private static string EncodeKey(string key) => Helpers.Base64UrlEncode(key);

    private void SetRequestBody(string content)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Request = { Body = stream } }
        };
    }

    [Fact]
    public void GetRoot_ShouldReturnOkWithVersion()
    {
        // Arrange
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        // Act
        var result = _controller.GetRoot() as OkObjectResult;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("OK ; Version:", result.Value?.ToString());
    }

    [Fact]
    public async Task SetBox_ShouldReturnJsonOnSuccess()
    {
        // Arrange
        var encodedKey = EncodeKey("mybox.mytag.v1");
        SetRequestBody("my secret value");

        // Act
        var result = await _controller.SetBox(encodedKey) as OkObjectResult;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(200, result.StatusCode);
        var apiResult = result.Value as ApiResult;
        Assert.NotNull(apiResult);
        Assert.Equal("ok", apiResult.Status);
        Assert.Equal("mybox.mytag.v1", apiResult.Key);
    }

    [Fact]
    public async Task SetBox_ShouldStoreValueInService()
    {
        // Arrange
        var encodedKey = EncodeKey("mybox.mytag.v1");
        SetRequestBody("stored value");

        // Act
        await _controller.SetBox(encodedKey);

        // Assert
        var stored = await _storageService.GetAsync(new KeyStore("mybox.mytag.v1"));
        Assert.Equal("stored value", stored);
    }

    [Fact]
    public async Task GetBox_ShouldReturnContentWhenKeyExists()
    {
        // Arrange
        var key = new KeyStore("mybox.mytag.v1");
        await _storageService.SetAsync(key, "the secret");
        var encodedKey = EncodeKey("mybox.mytag.v1");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        // Act
        var result = await _controller.GetBox(encodedKey) as ContentResult;

        // Assert
        Assert.NotNull(result);
        Assert.Equal("the secret", result.Content);
        Assert.Equal("text/plain; charset=utf-8", result.ContentType);
    }

    [Fact]
    public async Task GetBox_ShouldThrowStashLockExceptionWhenKeyMissing()
    {
        // Arrange
        var encodedKey = EncodeKey("missing.key.v1");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<StashLockException>(() => _controller.GetBox(encodedKey));
        Assert.Equal(ErrorCodes.KeyNotFound, ex.ErrorCode);
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task SetBox_ShouldThrowForInvalidBase64()
    {
        // Arrange
        SetRequestBody("some value");

        // Act & Assert
        await Assert.ThrowsAsync<FormatException>(() => _controller.SetBox("!!!invalid!!!"));
    }

    [Fact]
    public async Task SetBox_ShouldThrowForMalformedKey()
    {
        // Arrange — key with only 2 parts instead of 3
        var encodedKey = EncodeKey("onlytwo.parts");
        SetRequestBody("some value");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _controller.SetBox(encodedKey));
    }

    [Fact]
    public async Task GetMetadata_ShouldReturnMetadataWhenKeyExists()
    {
        // Arrange
        var key = new KeyStore("mybox.mytag.v1");
        await _storageService.SetAsync(key, "the secret");
        var encodedKey = EncodeKey("mybox.mytag.v1");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        // Act
        var result = await _controller.GetMetadata(encodedKey) as OkObjectResult;

        // Assert
        Assert.NotNull(result);
        var metadata = result.Value as KeyMetadata;
        Assert.NotNull(metadata);
        Assert.Equal(10, metadata.SizeBytes);
        Assert.False(metadata.IsDeleted);
    }

    [Fact]
    public async Task ListBoxes_ShouldReturnAllKeys()
    {
        // Arrange
        await _storageService.SetAsync(new KeyStore("box1.tag1.v1"), "val1");
        await _storageService.SetAsync(new KeyStore("box2.tag2.v1"), "val2");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        // Act
        var result = await _controller.ListBoxes() as OkObjectResult;

        // Assert
        Assert.NotNull(result);
        var keys = result.Value as List<KeyEntry>;
        Assert.NotNull(keys);
        Assert.Equal(2, keys.Count);
    }

    [Fact]
    public async Task ListBoxes_ShouldFilterByPrefix()
    {
        // Arrange
        await _storageService.SetAsync(new KeyStore("app1.secrets.v1"), "val1");
        await _storageService.SetAsync(new KeyStore("app2.config.v1"), "val2");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        // Act
        var result = await _controller.ListBoxes("app1") as OkObjectResult;

        // Assert
        Assert.NotNull(result);
        var keys = result.Value as List<KeyEntry>;
        Assert.NotNull(keys);
        Assert.Single(keys);
        Assert.Equal("app1", keys[0].Box);
    }

    [Fact]
    public async Task ListBoxes_ShouldExcludeDeleted()
    {
        // Arrange
        await _storageService.SetAsync(new KeyStore("live.tag.v1"), "val1");
        await _storageService.SetAsync(new KeyStore("dead.tag.v1"), "val2");
        await _storageService.DeleteAsync(new KeyStore("dead.tag.v1"));
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        // Act
        var result = await _controller.ListBoxes() as OkObjectResult;

        // Assert
        Assert.NotNull(result);
        var keys = result.Value as List<KeyEntry>;
        Assert.NotNull(keys);
        Assert.Single(keys);
        Assert.Equal("live.tag.v1", keys[0].Key);
    }

    [Fact]
    public async Task GetMetadata_ShouldThrowWhenKeyNotFound()
    {
        // Arrange
        var encodedKey = EncodeKey("missing.meta.v1");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<StashLockException>(() => _controller.GetMetadata(encodedKey));
        Assert.Equal(ErrorCodes.KeyNotFound, ex.ErrorCode);
    }

    private class FakeStorageService : IStorageService
    {
        private readonly Dictionary<string, (string Value, bool IsDeleted)> _store = new();

        public Task SetAsync(KeyStore key, string value, string? expiresAt = null)
        {
            _store[key.FullAddress] = (value, false);
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(KeyStore key)
        {
            if (_store.TryGetValue(key.FullAddress, out var entry) && !entry.IsDeleted)
                return Task.FromResult<string?>(entry.Value);
            return Task.FromResult<string?>(null);
        }

        public Task DeleteAsync(KeyStore key)
        {
            if (!_store.TryGetValue(key.FullAddress, out var entry))
                throw new StashLockException(ErrorCodes.KeyNotFound, $"Key not found: {key.FullAddress}", 404);
            if (entry.IsDeleted)
                throw new StashLockException(ErrorCodes.AlreadyDeleted, $"Key already deleted: {key.FullAddress}", 409);
            _store[key.FullAddress] = (entry.Value, true);
            return Task.CompletedTask;
        }

        public Task<KeyMetadata?> GetMetadataAsync(KeyStore key)
        {
            if (!_store.TryGetValue(key.FullAddress, out var entry))
                return Task.FromResult<KeyMetadata?>(null);
            return Task.FromResult<KeyMetadata?>(new KeyMetadata
            {
                CreatedAt = "2026-01-01T00:00:00Z",
                UpdatedAt = "2026-01-01T00:00:00Z",
                SizeBytes = System.Text.Encoding.UTF8.GetByteCount(entry.Value),
                IsDeleted = entry.IsDeleted
            });
        }

        public Task<List<KeyEntry>> ListKeysAsync(string? prefix = null)
        {
            var result = _store
                .Where(kv => !kv.Value.IsDeleted)
                .Where(kv => string.IsNullOrWhiteSpace(prefix) || kv.Key.StartsWith(prefix))
                .OrderBy(kv => kv.Key)
                .Select(kv =>
                {
                    var parts = kv.Key.Split('.', 3);
                    return new KeyEntry
                    {
                        Key = kv.Key,
                        Box = parts.Length > 0 ? parts[0] : "",
                        Tag = parts.Length > 1 ? parts[1] : "",
                        Version = parts.Length > 2 ? parts[2] : "",
                        CreatedAt = "2026-01-01T00:00:00Z",
                        UpdatedAt = "2026-01-01T00:00:00Z",
                        SizeBytes = Encoding.UTF8.GetByteCount(kv.Value.Value)
                    };
                }).ToList();
            return Task.FromResult(result);
        }

        public Task<List<HistoryEntry>> GetHistoryAsync(KeyStore key)
        {
            return Task.FromResult(new List<HistoryEntry>());
        }

        public Task<string?> GetVersionAsync(KeyStore key, int versionNumber)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private class FakeAuditLogService : IAuditLogService
    {
        public List<(string Action, string? BoxKey, string Outcome)> Entries { get; } = new();

        public Task LogAsync(string action, string? boxKey, string? apiKeyId, string? apiKeyName,
            string? correlationId, string outcome, string? detail = null)
        {
            Entries.Add((action, boxKey, outcome));
            return Task.CompletedTask;
        }

        public Task<List<AuditLogEntity>> QueryAsync(string? boxKey = null, string? action = null, int limit = 100)
        {
            return Task.FromResult(new List<AuditLogEntity>());
        }
    }

    [Fact]
    public async Task SetBox_ShouldCreateAuditEntry()
    {
        // Arrange
        var encodedKey = EncodeKey("audit.set.v1");
        SetRequestBody("audit test");

        // Act
        await _controller.SetBox(encodedKey);

        // Assert
        Assert.Contains(_auditLogService.Entries, e => e.Action == "SET" && e.BoxKey == "audit.set.v1" && e.Outcome == "success");
    }

    [Fact]
    public async Task GetBox_ShouldCreateAuditEntry()
    {
        // Arrange
        var key = new KeyStore("audit.get.v1");
        await _storageService.SetAsync(key, "value");
        var encodedKey = EncodeKey("audit.get.v1");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        // Act
        await _controller.GetBox(encodedKey);

        // Assert
        Assert.Contains(_auditLogService.Entries, e => e.Action == "GET" && e.BoxKey == "audit.get.v1" && e.Outcome == "success");
    }

    [Fact]
    public async Task GetBox_NotFound_ShouldCreateAuditEntry()
    {
        // Arrange
        var encodedKey = EncodeKey("audit.miss.v1");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        // Act & Assert
        await Assert.ThrowsAsync<StashLockException>(() => _controller.GetBox(encodedKey));
        Assert.Contains(_auditLogService.Entries, e => e.Action == "GET" && e.Outcome == "not_found");
    }

    [Fact]
    public async Task DeleteBox_ShouldCreateAuditEntry()
    {
        // Arrange
        var key = new KeyStore("audit.del.v1");
        await _storageService.SetAsync(key, "to delete");
        var encodedKey = EncodeKey("audit.del.v1");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        // Act
        await _controller.DeleteBox(encodedKey);

        // Assert
        Assert.Contains(_auditLogService.Entries, e => e.Action == "DELETE" && e.Outcome == "success");
    }

}
