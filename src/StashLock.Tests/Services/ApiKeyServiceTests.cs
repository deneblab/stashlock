using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Deneblab.StashLock.Server.Common.Simple;
using Deneblab.StashLock.Server.Data;
using Deneblab.StashLock.Server.Exceptions;
using Deneblab.StashLock.Server.Models;
using Deneblab.StashLock.Server.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Deneblab.StashLock.Tests.Services;

public class ApiKeyServiceTests : IDisposable
{
    private readonly StashLockDbContext _context;
    private readonly ApiKeyService _service;

    public ApiKeyServiceTests()
    {
        var options = new DbContextOptionsBuilder<StashLockDbContext>()
            .UseInMemoryDatabase(databaseName: "ApiKeyTests-" + Guid.NewGuid())
            .Options;

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

        _context = new StashLockDbContext(options, registry);
        _service = new ApiKeyService(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    [Fact]
    public async Task CreateAsync_ShouldReturnKeyWithSlkPrefix()
    {
        var request = new CreateApiKeyRequest { Name = "test-key" };

        var (info, plaintextKey) = await _service.CreateAsync(request);

        Assert.StartsWith("slk_", plaintextKey);
        Assert.True(plaintextKey.Length >= 40);
    }

    [Fact]
    public async Task CreateAsync_ShouldReturnInfoWithCorrectFields()
    {
        var request = new CreateApiKeyRequest
        {
            Name = "my-service",
            Scopes = new List<string> { "mybox" },
            CanRead = true,
            CanWrite = false,
            CanDelete = false
        };

        var (info, _) = await _service.CreateAsync(request);

        Assert.Equal("my-service", info.Name);
        Assert.Single(info.Scopes);
        Assert.Contains("mybox", info.Scopes);
        Assert.True(info.CanRead);
        Assert.False(info.CanWrite);
        Assert.False(info.CanDelete);
        Assert.True(info.IsActive);
        Assert.NotEmpty(info.Id);
        Assert.NotEmpty(info.KeyPrefix);
        Assert.NotEmpty(info.CreatedAt);
    }

    [Fact]
    public async Task CreateAsync_ShouldDefaultToWildcardScope()
    {
        var request = new CreateApiKeyRequest { Name = "wildcard-key" };

        var (info, _) = await _service.CreateAsync(request);

        Assert.Contains("*", info.Scopes);
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowForEmptyName()
    {
        var request = new CreateApiKeyRequest { Name = "" };

        var ex = await Assert.ThrowsAsync<StashLockException>(() => _service.CreateAsync(request));
        Assert.Equal(ErrorCodes.ValueRequired, ex.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowForWhitespaceName()
    {
        var request = new CreateApiKeyRequest { Name = "   " };

        var ex = await Assert.ThrowsAsync<StashLockException>(() => _service.CreateAsync(request));
        Assert.Equal(ErrorCodes.ValueRequired, ex.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_ShouldReturnIdentityForValidKey()
    {
        var request = new CreateApiKeyRequest
        {
            Name = "valid-key",
            Scopes = new List<string> { "app1", "app2" },
            CanRead = true,
            CanWrite = true,
            CanDelete = false
        };
        var (_, plaintextKey) = await _service.CreateAsync(request);

        var identity = await _service.ValidateAsync(plaintextKey);

        Assert.NotNull(identity);
        Assert.Equal("valid-key", identity.Name);
        Assert.False(identity.IsMaster);
        Assert.True(identity.CanRead);
        Assert.True(identity.CanWrite);
        Assert.False(identity.CanDelete);
        Assert.Contains("app1", identity.Scopes);
        Assert.Contains("app2", identity.Scopes);
    }

    [Fact]
    public async Task ValidateAsync_ShouldReturnNullForUnknownKey()
    {
        var identity = await _service.ValidateAsync("slk_nonexistent12345678901234567890");

        Assert.Null(identity);
    }

    [Fact]
    public async Task ValidateAsync_ShouldReturnNullForRevokedKey()
    {
        var (info, plaintextKey) = await _service.CreateAsync(new CreateApiKeyRequest { Name = "revoke-me" });
        await _service.RevokeAsync(info.Id);

        var identity = await _service.ValidateAsync(plaintextKey);

        Assert.Null(identity);
    }

    [Fact]
    public async Task ValidateAsync_ShouldReturnNullForExpiredKey()
    {
        var request = new CreateApiKeyRequest
        {
            Name = "expired-key",
            ExpiresAt = DateTime.UtcNow.AddHours(-1).ToString("O")
        };
        var (_, plaintextKey) = await _service.CreateAsync(request);

        var identity = await _service.ValidateAsync(plaintextKey);

        Assert.Null(identity);
    }

    [Fact]
    public async Task ValidateAsync_ShouldReturnIdentityForNonExpiredKey()
    {
        var request = new CreateApiKeyRequest
        {
            Name = "future-key",
            ExpiresAt = DateTime.UtcNow.AddHours(24).ToString("O")
        };
        var (_, plaintextKey) = await _service.CreateAsync(request);

        var identity = await _service.ValidateAsync(plaintextKey);

        Assert.NotNull(identity);
    }

    [Fact]
    public async Task ListAsync_ShouldReturnAllKeys()
    {
        await _service.CreateAsync(new CreateApiKeyRequest { Name = "key-a" });
        await _service.CreateAsync(new CreateApiKeyRequest { Name = "key-b" });

        var keys = await _service.ListAsync();

        Assert.Equal(2, keys.Count);
    }

    [Fact]
    public async Task ListAsync_ShouldReturnOrderedByName()
    {
        await _service.CreateAsync(new CreateApiKeyRequest { Name = "zulu" });
        await _service.CreateAsync(new CreateApiKeyRequest { Name = "alpha" });

        var keys = await _service.ListAsync();

        Assert.Equal("alpha", keys[0].Name);
        Assert.Equal("zulu", keys[1].Name);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnKeyById()
    {
        var (info, _) = await _service.CreateAsync(new CreateApiKeyRequest { Name = "findme" });

        var result = await _service.GetAsync(info.Id);

        Assert.NotNull(result);
        Assert.Equal("findme", result.Name);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNullForUnknownId()
    {
        var result = await _service.GetAsync("nonexistent-id");

        Assert.Null(result);
    }

    [Fact]
    public async Task RevokeAsync_ShouldDeactivateKey()
    {
        var (info, _) = await _service.CreateAsync(new CreateApiKeyRequest { Name = "to-revoke" });

        await _service.RevokeAsync(info.Id);

        var result = await _service.GetAsync(info.Id);
        Assert.NotNull(result);
        Assert.False(result.IsActive);
    }

    [Fact]
    public async Task RevokeAsync_ShouldThrowForUnknownId()
    {
        var ex = await Assert.ThrowsAsync<StashLockException>(() => _service.RevokeAsync("no-such-id"));
        Assert.Equal(ErrorCodes.KeyNotFound, ex.ErrorCode);
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task CreateAsync_ShouldGenerateUniqueKeys()
    {
        var (_, key1) = await _service.CreateAsync(new CreateApiKeyRequest { Name = "unique-1" });
        var (_, key2) = await _service.CreateAsync(new CreateApiKeyRequest { Name = "unique-2" });

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public async Task RateLimitPerMinute_ShouldRoundTripThroughCreateAndValidate()
    {
        var request = new CreateApiKeyRequest
        {
            Name = "rate-limited",
            RateLimitPerMinute = 30
        };
        var (info, plaintextKey) = await _service.CreateAsync(request);

        Assert.Equal(30, info.RateLimitPerMinute);

        var identity = await _service.ValidateAsync(plaintextKey);
        Assert.NotNull(identity);
        Assert.Equal(30, identity.RateLimitPerMinute);
    }

    [Fact]
    public async Task RateLimitPerMinute_NullByDefault()
    {
        var request = new CreateApiKeyRequest { Name = "no-rate-limit" };
        var (info, plaintextKey) = await _service.CreateAsync(request);

        Assert.Null(info.RateLimitPerMinute);

        var identity = await _service.ValidateAsync(plaintextKey);
        Assert.NotNull(identity);
        Assert.Null(identity.RateLimitPerMinute);
    }

    [Fact]
    public async Task ValidateAsync_SameKeyValidatesTwice()
    {
        var (_, plaintextKey) = await _service.CreateAsync(new CreateApiKeyRequest { Name = "deterministic" });

        var identity1 = await _service.ValidateAsync(plaintextKey);
        var identity2 = await _service.ValidateAsync(plaintextKey);

        Assert.NotNull(identity1);
        Assert.NotNull(identity2);
        Assert.Equal(identity1.Id, identity2.Id);
    }
}
