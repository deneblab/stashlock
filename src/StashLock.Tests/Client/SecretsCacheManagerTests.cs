using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Deneblab.StashLock.Client.Common;
using Xunit;

namespace Deneblab.StashLock.Tests.Client;

public class SecretsCacheManagerTests : IDisposable
{
    private readonly string _tempDir;

    public SecretsCacheManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"stashlock-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static byte[] GenerateKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    [Fact]
    public void DeriveCacheKey_Returns32Bytes()
    {
        var privateKey = GenerateKey();
        var cacheKey = SecretsCacheManager.DeriveCacheKey(privateKey, "test-machine");
        Assert.Equal(32, cacheKey.Length);
    }

    [Fact]
    public void DeriveCacheKey_DeterministicForSameInputs()
    {
        var privateKey = GenerateKey();
        var key1 = SecretsCacheManager.DeriveCacheKey(privateKey, "machine-A");
        var key2 = SecretsCacheManager.DeriveCacheKey(privateKey, "machine-A");
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void DeriveCacheKey_DifferentForDifferentMachineId()
    {
        var privateKey = GenerateKey();
        var key1 = SecretsCacheManager.DeriveCacheKey(privateKey, "machine-A");
        var key2 = SecretsCacheManager.DeriveCacheKey(privateKey, "machine-B");
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void DeriveCacheKey_DifferentForDifferentPrivateKey()
    {
        var pk1 = GenerateKey();
        var pk2 = GenerateKey();
        var key1 = SecretsCacheManager.DeriveCacheKey(pk1, "same-machine");
        var key2 = SecretsCacheManager.DeriveCacheKey(pk2, "same-machine");
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void WriteAndRead_RoundTripsSecrets()
    {
        var privateKey = GenerateKey();
        var cacheKey = SecretsCacheManager.DeriveCacheKey(privateKey, "test-machine");
        var filePath = Path.Combine(_tempDir, "test.bin");

        var secrets = new Dictionary<string, string>
        {
            ["Database:ConnectionString"] = "Server=localhost;Database=test",
            ["Api:Key"] = "sk-abc123",
            ["Feature:Enabled"] = "true"
        };

        SecretsCacheManager.WriteCache(filePath, secrets, cacheKey, TimeSpan.FromHours(1));

        var read = SecretsCacheManager.ReadCache(filePath, cacheKey);
        Assert.NotNull(read);
        Assert.Equal(3, read.Count);
        Assert.Equal("Server=localhost;Database=test", read["Database:ConnectionString"]);
        Assert.Equal("sk-abc123", read["Api:Key"]);
        Assert.Equal("true", read["Feature:Enabled"]);
    }

    [Fact]
    public void ReadCache_ReturnsNullForExpiredCache()
    {
        var privateKey = GenerateKey();
        var cacheKey = SecretsCacheManager.DeriveCacheKey(privateKey, "test-machine");
        var filePath = Path.Combine(_tempDir, "expired.bin");

        var secrets = new Dictionary<string, string> { ["key"] = "value" };

        // Write with 0-second TTL (immediately expired)
        SecretsCacheManager.WriteCache(filePath, secrets, cacheKey, TimeSpan.Zero);

        // Wait a tiny bit to ensure expiry
        System.Threading.Thread.Sleep(50);

        var read = SecretsCacheManager.ReadCache(filePath, cacheKey);
        Assert.Null(read);
    }

    [Fact]
    public void ReadCache_ReturnsNullForWrongKey()
    {
        var pk1 = GenerateKey();
        var pk2 = GenerateKey();
        var cacheKey1 = SecretsCacheManager.DeriveCacheKey(pk1, "machine-A");
        var cacheKey2 = SecretsCacheManager.DeriveCacheKey(pk2, "machine-B");
        var filePath = Path.Combine(_tempDir, "wrongkey.bin");

        var secrets = new Dictionary<string, string> { ["key"] = "value" };
        SecretsCacheManager.WriteCache(filePath, secrets, cacheKey1, TimeSpan.FromHours(1));

        // Read with a different key — should fail silently
        var read = SecretsCacheManager.ReadCache(filePath, cacheKey2);
        Assert.Null(read);
    }

    [Fact]
    public void ReadCache_ReturnsNullForMissingFile()
    {
        var cacheKey = SecretsCacheManager.DeriveCacheKey(GenerateKey(), "test");
        var read = SecretsCacheManager.ReadCache(Path.Combine(_tempDir, "nonexistent.bin"), cacheKey);
        Assert.Null(read);
    }

    [Fact]
    public void ReadCache_ReturnsNullForCorruptedFile()
    {
        var cacheKey = SecretsCacheManager.DeriveCacheKey(GenerateKey(), "test");
        var filePath = Path.Combine(_tempDir, "corrupt.bin");
        File.WriteAllBytes(filePath, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 });

        var read = SecretsCacheManager.ReadCache(filePath, cacheKey);
        Assert.Null(read);
    }

    [Fact]
    public void ReadCache_ReturnsNullForWrongMagic()
    {
        var cacheKey = SecretsCacheManager.DeriveCacheKey(GenerateKey(), "test");
        var filePath = Path.Combine(_tempDir, "wrongmagic.bin");

        // Write a file with wrong magic bytes but correct length
        var data = new byte[100];
        data[0] = (byte)'X'; data[1] = (byte)'X'; data[2] = (byte)'X'; data[3] = (byte)'X';
        File.WriteAllBytes(filePath, data);

        var read = SecretsCacheManager.ReadCache(filePath, cacheKey);
        Assert.Null(read);
    }

    [Fact]
    public void GetCacheFilePath_BuildsCorrectPath()
    {
        var path = SecretsCacheManager.GetCacheFilePath("/app/cache", "myapp", "production", "00001");
        Assert.EndsWith("stashlock.cache.myapp.production.00001.bin", path);
    }

    [Fact]
    public void WriteCache_CreatesDirectoryIfMissing()
    {
        var privateKey = GenerateKey();
        var cacheKey = SecretsCacheManager.DeriveCacheKey(privateKey, "test");
        var nestedDir = Path.Combine(_tempDir, "nested", "deep", "cache");
        var filePath = Path.Combine(nestedDir, "test.bin");

        var secrets = new Dictionary<string, string> { ["k"] = "v" };
        SecretsCacheManager.WriteCache(filePath, secrets, cacheKey, TimeSpan.FromHours(1));

        Assert.True(File.Exists(filePath));

        var read = SecretsCacheManager.ReadCache(filePath, cacheKey);
        Assert.NotNull(read);
        Assert.Equal("v", read["k"]);
    }

    [Fact]
    public void WriteCache_OverwritesExistingFile()
    {
        var privateKey = GenerateKey();
        var cacheKey = SecretsCacheManager.DeriveCacheKey(privateKey, "test");
        var filePath = Path.Combine(_tempDir, "overwrite.bin");

        SecretsCacheManager.WriteCache(filePath,
            new Dictionary<string, string> { ["k"] = "old" }, cacheKey, TimeSpan.FromHours(1));

        SecretsCacheManager.WriteCache(filePath,
            new Dictionary<string, string> { ["k"] = "new" }, cacheKey, TimeSpan.FromHours(1));

        var read = SecretsCacheManager.ReadCache(filePath, cacheKey);
        Assert.NotNull(read);
        Assert.Equal("new", read["k"]);
    }

    [Fact]
    public void WriteAndRead_HandlesEmptyDictionary()
    {
        var cacheKey = SecretsCacheManager.DeriveCacheKey(GenerateKey(), "test");
        var filePath = Path.Combine(_tempDir, "empty.bin");

        SecretsCacheManager.WriteCache(filePath, new Dictionary<string, string>(), cacheKey, TimeSpan.FromHours(1));

        var read = SecretsCacheManager.ReadCache(filePath, cacheKey);
        Assert.NotNull(read);
        Assert.Empty(read);
    }

    [Fact]
    public void WriteAndRead_HandlesUnicodeValues()
    {
        var cacheKey = SecretsCacheManager.DeriveCacheKey(GenerateKey(), "test");
        var filePath = Path.Combine(_tempDir, "unicode.bin");

        var secrets = new Dictionary<string, string>
        {
            ["greeting"] = "héllo wörld! 日本語 🔐",
            ["path"] = "C:\\Users\\ąść\\file.txt"
        };

        SecretsCacheManager.WriteCache(filePath, secrets, cacheKey, TimeSpan.FromHours(1));

        var read = SecretsCacheManager.ReadCache(filePath, cacheKey);
        Assert.NotNull(read);
        Assert.Equal("héllo wörld! 日本語 🔐", read["greeting"]);
        Assert.Equal("C:\\Users\\ąść\\file.txt", read["path"]);
    }
}
