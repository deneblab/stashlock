using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Deneblab.StashLock.Client.Common;
using Deneblab.StashLock.Client.Common.Exceptions;
using Deneblab.StashLock.Shared.Crypto;
using Deneblab.StashLock.Shared.Formats;
using Xunit;

namespace Deneblab.StashLock.Tests.Formats;

public class SopsCodecTests
{
    private static readonly Dictionary<string, string> Secrets = new()
    {
        ["Db:Password"] = "s3cret",
        ["Db:Port"] = "5432",
        ["ApiKey"] = "abc"
    };

    private static (byte[] Public, byte[] Private) Keys() => Ecies.GenerateKeyPair();

    private static string Json(SopsEncryptedFile f) => JsonSerializer.Serialize(f);

    [Fact]
    public void RoundTrip()
    {
        var (pub, priv) = Keys();
        var file = SopsCodec.Encrypt(Secrets, pub, "app.dev.00001");
        Assert.Equal("sops", file.Metadata.Mode);
        var result = SopsDecryptor.DecryptAutoDetect(Json(file), priv, "app.dev.00001");
        Assert.Equal(Secrets.OrderBy(k => k.Key), result.OrderBy(k => k.Key));
    }

    [Fact]
    public void ValueMovedToAnotherKey_IsRejected()
    {
        var (pub, priv) = Keys();
        var file = SopsCodec.Encrypt(Secrets, pub, "app.dev.00001");
        (file.Values["ApiKey"], file.Values["Db:Password"]) = (file.Values["Db:Password"], file.Values["ApiKey"]);
        Assert.Throws<CryptographicException>(() => SopsCodec.Decrypt(file, priv));
    }

    [Fact]
    public void RemovedOrAddedKey_FailsMac()
    {
        var (pub, priv) = Keys();
        var removed = SopsCodec.Encrypt(Secrets, pub, "app.dev.00001");
        removed.Values.Remove("ApiKey");
        Assert.Contains("MAC", Assert.Throws<CryptographicException>(() => SopsCodec.Decrypt(removed, priv)).Message);

        var added = SopsCodec.Encrypt(Secrets, pub, "app.dev.00001");
        added.Values["Extra"] = added.Values["ApiKey"];
        Assert.Contains("MAC", Assert.Throws<CryptographicException>(() => SopsCodec.Decrypt(added, priv)).Message);
    }

    [Fact]
    public void VaultKeyIsBound()
    {
        var (pub, priv) = Keys();

        var relabelled = SopsCodec.Encrypt(Secrets, pub, "app.dev.00001");
        relabelled.Metadata.VaultKey = "app.prod.00001";
        Assert.Contains("MAC", Assert.Throws<CryptographicException>(() => SopsCodec.Decrypt(relabelled, priv)).Message);

        // A genuine file for another entry is refused when the client knows what it asked for
        var other = SopsCodec.Encrypt(Secrets, pub, "app.prod.00001");
        Assert.Throws<DecryptionException>(() => SopsDecryptor.DecryptAutoDetect(Json(other), priv, "app.dev.00001"));
    }

    [Fact]
    public void OtherModes_AreRejected()
    {
        var (pub, priv) = Keys();
        var file = SopsCodec.Encrypt(Secrets, pub, "app.dev.00001");
        file.Metadata.Mode = "sops-v2";
        Assert.Contains("Unsupported", Assert.Throws<CryptographicException>(() => SopsCodec.Decrypt(file, priv)).Message);
        Assert.Throws<DecryptionException>(() => SopsDecryptor.DecryptAutoDetect("{\"VaultKey\":\"a.b.c\",\"EncryptedData\":\"AAAA\"}", priv));
    }

    [Fact]
    public void WrongPrivateKey_IsReported()
    {
        var (pub, _) = Keys();
        var (_, otherPriv) = Keys();
        var file = SopsCodec.Encrypt(Secrets, pub, "app.dev.00001");
        Assert.Contains("wrong private key", Assert.Throws<CryptographicException>(() => SopsCodec.Decrypt(file, otherPriv)).Message);
    }
}

public class CacheFormatTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("stashlock-cache2-").FullName;
    private readonly byte[] _key = SecretsCacheManager.DeriveCacheKey(new byte[32], "test-machine");
    private static readonly Dictionary<string, string> Secrets = new() { ["Db"] = "secret" };

    public void Dispose() => Directory.Delete(_dir, true);

    private string Write(string name)
    {
        var path = Path.Combine(_dir, name);
        SecretsCacheManager.WriteCache(path, Secrets, _key, TimeSpan.FromHours(1));
        return path;
    }

    [Fact]
    public void RoundTrip()
    {
        var path = Write("stashlock.cache.app.dev.00001.bin");
        Assert.Equal("secret", SecretsCacheManager.ReadCache(path, _key)!["Db"]);
    }

    [Fact]
    public void ChangedTtl_IsRejected()
    {
        var path = Write("stashlock.cache.app.dev.00001.bin");
        var bytes = File.ReadAllBytes(path);
        BitConverter.TryWriteBytes(bytes.AsSpan(12, 4), int.MaxValue); // TTL field
        File.WriteAllBytes(path, bytes);
        Assert.Null(SecretsCacheManager.ReadCache(path, _key, ignoreTtl: true));
    }

    [Fact]
    public void ChangedCreatedAt_IsRejected()
    {
        var path = Write("stashlock.cache.app.dev.00001.bin");
        var bytes = File.ReadAllBytes(path);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 8), DateTimeOffset.UtcNow.AddYears(1).UtcTicks);
        File.WriteAllBytes(path, bytes);
        Assert.Null(SecretsCacheManager.ReadCache(path, _key, ignoreTtl: true));
    }

    [Fact]
    public void CopiedOverAnotherEntry_IsRejected()
    {
        var source = Write("stashlock.cache.app.dev.00001.bin");
        var target = Path.Combine(_dir, "stashlock.cache.app.prod.00001.bin");
        File.Copy(source, target);
        Assert.Null(SecretsCacheManager.ReadCache(target, _key, ignoreTtl: true));
    }

    [Fact]
    public void OldSlc1File_IsAMiss()
    {
        var path = Write("stashlock.cache.app.dev.00001.bin");
        var bytes = File.ReadAllBytes(path);
        "SLC1"u8.ToArray().CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
        Assert.Null(SecretsCacheManager.ReadCache(path, _key, ignoreTtl: true));
    }
}
