using System;
using System.IO;
using System.Threading.Tasks;
using Deneblab.StashLock.Server.Common.Simple;
using Deneblab.StashLock.Server.Data;
using Deneblab.StashLock.Server.Exceptions;
using Deneblab.StashLock.Server.Models;
using Deneblab.StashLock.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NLog.Extensions.Logging;
using Xunit;

namespace Deneblab.StashLock.Tests.Services;

public class EfCoreStorageServiceTests : IDisposable
{
    private readonly StashLockDbContext _context;
    private readonly EfCoreStorageService _service;
    private readonly Registry _registry;

    public EfCoreStorageServiceTests()
    {
        // Create in-memory database for testing
        var options = new DbContextOptionsBuilder<StashLockDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        // Create mock registry
        var env = new SimpleEnvResult(
            SimpleAppMode.Test,
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath()
        );
        var version = new SimpleVersionInfo(
            "TestProduct",
            "1.0.0-test",
            0,
            "test",
            DateTime.UtcNow,
            "test",
            "abc123",
            0,
            new Dictionary<string, string>()
        );
        _registry = new Registry(env, version);

        _context = new StashLockDbContext(options, _registry);
        var logger = LoggerFactory.Create(builder => builder.AddNLog())
            .CreateLogger<EfCoreStorageService>();
        var stashLockOptions = Options.Create(new StashLockOptions());
        _service = new EfCoreStorageService(_context, logger, stashLockOptions);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    [Fact]
    public async Task SetAsync_ShouldStoreValue()
    {
        // Arrange
        var key = new KeyStore("testbox.testtag.v1");
        var value = "test value";

        // Act
        await _service.SetAsync(key, value);

        // Assert
        var retrieved = await _service.GetAsync(key);
        Assert.Equal(value, retrieved);
    }

    [Fact]
    public async Task SetAsync_ShouldUpdateExistingValue()
    {
        // Arrange
        var key = new KeyStore("testbox.testtag.v1");
        var value1 = "first value";
        var value2 = "second value";

        // Act
        await _service.SetAsync(key, value1);
        await _service.SetAsync(key, value2);

        // Assert
        var retrieved = await _service.GetAsync(key);
        Assert.Equal(value2, retrieved);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNullForNonExistentKey()
    {
        // Arrange
        var key = new KeyStore("nonexistent.box.v1");

        // Act
        var result = await _service.GetAsync(key);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_ShouldThrowForNullValue()
    {
        // Arrange
        var key = new KeyStore("testbox.testtag.v1");

        // Act & Assert
        var ex = await Assert.ThrowsAsync<StashLockException>(() => _service.SetAsync(key, null!));
        Assert.Equal(ErrorCodes.ValueRequired, ex.ErrorCode);
    }

    [Fact]
    public async Task SetAsync_ShouldThrowForEmptyValue()
    {
        // Arrange
        var key = new KeyStore("testbox.testtag.v1");

        // Act & Assert
        var ex = await Assert.ThrowsAsync<StashLockException>(() => _service.SetAsync(key, string.Empty));
        Assert.Equal(ErrorCodes.ValueRequired, ex.ErrorCode);
    }

    [Fact]
    public async Task SetAsync_ShouldThrowForValueExceedingMaxSize()
    {
        // Arrange
        var key = new KeyStore("testbox.testtag.v1");
        var largeValue = new string('x', 101 * 1024); // 101kb (exceeds 100kb limit)

        // Act & Assert
        var ex = await Assert.ThrowsAsync<StashLockException>(() => _service.SetAsync(key, largeValue));
        Assert.Equal(ErrorCodes.ValueTooLarge, ex.ErrorCode);
    }

    [Fact]
    public async Task SetAsync_ShouldAcceptValueAtMaxSize()
    {
        // Arrange
        var key = new KeyStore("testbox.testtag.v1");
        var maxValue = new string('x', 100 * 1024); // Exactly 100kb

        // Act
        await _service.SetAsync(key, maxValue);

        // Assert
        var retrieved = await _service.GetAsync(key);
        Assert.Equal(maxValue, retrieved);
    }

    [Fact]
    public async Task SetAsync_ShouldHandleMultipleKeys()
    {
        // Arrange
        var key1 = new KeyStore("box1.tag1.v1");
        var key2 = new KeyStore("box2.tag2.v2");
        var value1 = "value 1";
        var value2 = "value 2";

        // Act
        await _service.SetAsync(key1, value1);
        await _service.SetAsync(key2, value2);

        // Assert
        var retrieved1 = await _service.GetAsync(key1);
        var retrieved2 = await _service.GetAsync(key2);
        Assert.Equal(value1, retrieved1);
        Assert.Equal(value2, retrieved2);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveExistingKey()
    {
        // Arrange
        var key = new KeyStore("testbox.testtag.v1");
        await _service.SetAsync(key, "some value");

        // Act
        await _service.DeleteAsync(key);

        // Assert
        var result = await _service.GetAsync(key);
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_ShouldThrowForNonExistentKey()
    {
        // Arrange
        var key = new KeyStore("nonexistent.box.v1");

        // Act & Assert
        var ex = await Assert.ThrowsAsync<StashLockException>(() => _service.DeleteAsync(key));
        Assert.Equal(ErrorCodes.KeyNotFound, ex.ErrorCode);
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task DeleteAsync_ShouldNotAffectOtherKeys()
    {
        // Arrange
        var key1 = new KeyStore("box1.tag1.v1");
        var key2 = new KeyStore("box2.tag2.v1");
        await _service.SetAsync(key1, "value1");
        await _service.SetAsync(key2, "value2");

        // Act
        await _service.DeleteAsync(key1);

        // Assert
        var result1 = await _service.GetAsync(key1);
        var result2 = await _service.GetAsync(key2);
        Assert.Null(result1);
        Assert.Equal("value2", result2);
    }

    [Fact]
    public async Task SoftDelete_EntityStillExistsInDb()
    {
        // Arrange
        var key = new KeyStore("softdel.test.v1");
        await _service.SetAsync(key, "to be deleted");

        // Act
        await _service.DeleteAsync(key);

        // Assert — entity still in DB
        var entity = await _context.KeyValues.FindAsync(key.FullAddress);
        Assert.NotNull(entity);
        Assert.True(entity.IsDeleted);
        Assert.NotNull(entity.DeletedAt);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForSoftDeleted()
    {
        // Arrange
        var key = new KeyStore("softget.test.v1");
        await _service.SetAsync(key, "secret");
        await _service.DeleteAsync(key);

        // Act
        var result = await _service.GetAsync(key);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_RestoresSoftDeletedKey()
    {
        // Arrange
        var key = new KeyStore("restore.test.v1");
        await _service.SetAsync(key, "original");
        await _service.DeleteAsync(key);

        // Act
        await _service.SetAsync(key, "restored value");

        // Assert
        var result = await _service.GetAsync(key);
        Assert.Equal("restored value", result);

        var entity = await _context.KeyValues.FindAsync(key.FullAddress);
        Assert.NotNull(entity);
        Assert.False(entity.IsDeleted);
        Assert.Null(entity.DeletedAt);
    }

    [Fact]
    public async Task DeleteAsync_ThrowsForAlreadyDeleted()
    {
        // Arrange
        var key = new KeyStore("deldel.test.v1");
        await _service.SetAsync(key, "to delete twice");
        await _service.DeleteAsync(key);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<StashLockException>(() => _service.DeleteAsync(key));
        Assert.Equal(ErrorCodes.AlreadyDeleted, ex.ErrorCode);
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task GetMetadataAsync_ReturnsMetadataForExistingKey()
    {
        // Arrange
        var key = new KeyStore("meta.test.v1");
        await _service.SetAsync(key, "hello world");

        // Act
        var meta = await _service.GetMetadataAsync(key);

        // Assert
        Assert.NotNull(meta);
        Assert.Equal(11, meta.SizeBytes);
        Assert.False(meta.IsDeleted);
        Assert.Null(meta.DeletedAt);
        Assert.NotEmpty(meta.CreatedAt);
        Assert.NotEmpty(meta.UpdatedAt);
    }

    [Fact]
    public async Task GetMetadataAsync_ReturnsNullForNonExistentKey()
    {
        // Arrange
        var key = new KeyStore("nometa.test.v1");

        // Act
        var meta = await _service.GetMetadataAsync(key);

        // Assert
        Assert.Null(meta);
    }

    [Fact]
    public async Task GetMetadataAsync_ShowsDeletedState()
    {
        // Arrange
        var key = new KeyStore("metadel.test.v1");
        await _service.SetAsync(key, "to be deleted");
        await _service.DeleteAsync(key);

        // Act
        var meta = await _service.GetMetadataAsync(key);

        // Assert
        Assert.NotNull(meta);
        Assert.True(meta.IsDeleted);
        Assert.NotNull(meta.DeletedAt);
    }

    [Fact]
    public async Task ListKeysAsync_ReturnsAllActiveKeys()
    {
        // Arrange
        await _service.SetAsync(new KeyStore("box1.tag1.v1"), "val1");
        await _service.SetAsync(new KeyStore("box2.tag2.v1"), "val2");

        // Act
        var keys = await _service.ListKeysAsync();

        // Assert
        Assert.Equal(2, keys.Count);
        Assert.Contains(keys, k => k.Key == "box1.tag1.v1");
        Assert.Contains(keys, k => k.Key == "box2.tag2.v1");
    }

    [Fact]
    public async Task ListKeysAsync_FiltersByPrefix()
    {
        // Arrange
        await _service.SetAsync(new KeyStore("app1.secrets.v1"), "s1");
        await _service.SetAsync(new KeyStore("app2.config.v1"), "c1");

        // Act
        var keys = await _service.ListKeysAsync("app1");

        // Assert
        Assert.Single(keys);
        Assert.Equal("app1", keys[0].Box);
    }

    [Fact]
    public async Task ListKeysAsync_ExcludesSoftDeleted()
    {
        // Arrange
        await _service.SetAsync(new KeyStore("live.key.v1"), "val");
        await _service.SetAsync(new KeyStore("dead.key.v1"), "val");
        await _service.DeleteAsync(new KeyStore("dead.key.v1"));

        // Act
        var keys = await _service.ListKeysAsync();

        // Assert
        Assert.Single(keys);
        Assert.Equal("live.key.v1", keys[0].Key);
    }

    [Fact]
    public async Task ListKeysAsync_ParsesKeyParts()
    {
        // Arrange
        await _service.SetAsync(new KeyStore("mybox.mytag.v2"), "val");

        // Act
        var keys = await _service.ListKeysAsync();

        // Assert
        Assert.Single(keys);
        Assert.Equal("mybox", keys[0].Box);
        Assert.Equal("mytag", keys[0].Tag);
        Assert.Equal("v2", keys[0].Version);
        Assert.True(keys[0].SizeBytes > 0);
    }

    // --- TTL / Expiration tests ---

    [Fact]
    public async Task SetAsync_StoresExpiresAt()
    {
        // Arrange
        var key = new KeyStore("ttl.store.v1");
        var expiresAt = DateTime.UtcNow.AddHours(1).ToString("O");

        // Act
        await _service.SetAsync(key, "ttl value", expiresAt);

        // Assert
        var entity = await _context.KeyValues.FindAsync(key.FullAddress);
        Assert.NotNull(entity);
        Assert.Equal(expiresAt, entity.ExpiresAt);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForExpiredKey()
    {
        // Arrange
        var key = new KeyStore("ttl.expired.v1");
        var expiresAt = DateTime.UtcNow.AddHours(-1).ToString("O");
        await _service.SetAsync(key, "expired value", expiresAt);

        // Act
        var result = await _service.GetAsync(key);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_ReturnsValueForNonExpiredKey()
    {
        // Arrange
        var key = new KeyStore("ttl.active.v1");
        var expiresAt = DateTime.UtcNow.AddHours(1).ToString("O");
        await _service.SetAsync(key, "active value", expiresAt);

        // Act
        var result = await _service.GetAsync(key);

        // Assert
        Assert.Equal("active value", result);
    }

    [Fact]
    public async Task ListKeysAsync_ExcludesExpiredKeys()
    {
        // Arrange
        var liveKey = new KeyStore("ttllist.live.v1");
        var expiredKey = new KeyStore("ttllist.dead.v1");
        await _service.SetAsync(liveKey, "live", DateTime.UtcNow.AddHours(1).ToString("O"));
        await _service.SetAsync(expiredKey, "dead", DateTime.UtcNow.AddHours(-1).ToString("O"));

        // Act
        var keys = await _service.ListKeysAsync();

        // Assert
        Assert.Single(keys);
        Assert.Equal("ttllist.live.v1", keys[0].Key);
    }

    [Fact]
    public async Task GetMetadataAsync_ShowsExpiresAtAndIsExpired()
    {
        // Arrange
        var key = new KeyStore("ttl.meta.v1");
        var expiresAt = DateTime.UtcNow.AddHours(-1).ToString("O");
        await _service.SetAsync(key, "expired meta", expiresAt);

        // Act
        var meta = await _service.GetMetadataAsync(key);

        // Assert
        Assert.NotNull(meta);
        Assert.Equal(expiresAt, meta.ExpiresAt);
        Assert.True(meta.IsExpired);
    }

    [Fact]
    public async Task SetAsync_UpdatesExpiresAtOnExistingKey()
    {
        // Arrange
        var key = new KeyStore("ttl.update.v1");
        await _service.SetAsync(key, "initial");

        // Act — set with TTL
        var expiresAt = DateTime.UtcNow.AddHours(2).ToString("O");
        await _service.SetAsync(key, "updated", expiresAt);

        // Assert
        var entity = await _context.KeyValues.FindAsync(key.FullAddress);
        Assert.NotNull(entity);
        Assert.Equal(expiresAt, entity.ExpiresAt);
    }

    // --- Secret Versioning / History tests ---

    [Fact]
    public async Task SetAsync_FirstWrite_CreatesNoHistory()
    {
        var key = new KeyStore("hist.first.v1");
        await _service.SetAsync(key, "initial value");

        var history = await _service.GetHistoryAsync(key);
        Assert.Empty(history);
    }

    [Fact]
    public async Task SetAsync_SecondWrite_CreatesOneHistoryEntry()
    {
        var key = new KeyStore("hist.second.v1");
        await _service.SetAsync(key, "value-1");
        await _service.SetAsync(key, "value-2");

        var history = await _service.GetHistoryAsync(key);
        Assert.Single(history);
        Assert.Equal(1, history[0].VersionNumber);
    }

    [Fact]
    public async Task SetAsync_ThirdWrite_CreatesTwoHistoryEntries()
    {
        var key = new KeyStore("hist.third.v1");
        await _service.SetAsync(key, "v1");
        await _service.SetAsync(key, "v2");
        await _service.SetAsync(key, "v3");

        var history = await _service.GetHistoryAsync(key);
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsEntriesOrderedByVersionDesc()
    {
        var key = new KeyStore("hist.order.v1");
        await _service.SetAsync(key, "a");
        await _service.SetAsync(key, "b");
        await _service.SetAsync(key, "c");

        var history = await _service.GetHistoryAsync(key);
        Assert.Equal(2, history.Count);
        Assert.True(history[0].VersionNumber > history[1].VersionNumber);
    }

    [Fact]
    public async Task GetVersionAsync_ReturnsCorrectOldValue()
    {
        var key = new KeyStore("hist.getver.v1");
        await _service.SetAsync(key, "original");
        await _service.SetAsync(key, "updated");

        var oldValue = await _service.GetVersionAsync(key, 1);
        Assert.Equal("original", oldValue);
    }

    [Fact]
    public async Task GetVersionAsync_ReturnsNullForNonExistentVersion()
    {
        var key = new KeyStore("hist.nover.v1");
        await _service.SetAsync(key, "only value");

        var result = await _service.GetVersionAsync(key, 99);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetMetadataAsync_ReturnsCorrectVersionCount()
    {
        var key = new KeyStore("hist.metacount.v1");
        await _service.SetAsync(key, "v1");
        await _service.SetAsync(key, "v2");
        await _service.SetAsync(key, "v3");

        var meta = await _service.GetMetadataAsync(key);
        Assert.NotNull(meta);
        Assert.Equal(2, meta.VersionCount);
    }

    [Fact]
    public async Task SetAsync_SoftDeletedRestore_DoesNotCreateHistory()
    {
        var key = new KeyStore("hist.restore.v1");
        await _service.SetAsync(key, "original");
        await _service.DeleteAsync(key);
        await _service.SetAsync(key, "restored");

        var history = await _service.GetHistoryAsync(key);
        Assert.Empty(history);
    }

    [Fact]
    public async Task SetAsync_ClearsExpiresAtWhenNotProvided()
    {
        // Arrange
        var key = new KeyStore("ttl.clear.v1");
        var expiresAt = DateTime.UtcNow.AddHours(1).ToString("O");
        await _service.SetAsync(key, "with ttl", expiresAt);

        // Act — overwrite without TTL
        await _service.SetAsync(key, "no ttl");

        // Assert
        var entity = await _context.KeyValues.FindAsync(key.FullAddress);
        Assert.NotNull(entity);
        Assert.Null(entity.ExpiresAt);
    }
}
