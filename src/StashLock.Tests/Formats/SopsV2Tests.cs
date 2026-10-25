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

public class SopsV2Tests
{
    // Written by the CLI before SOPS v2 existed (stashlock encode, format 1). Must keep decrypting.
    private const string V1File = """
    {
      "values": {
        "ApiKey": "ENC[AesGcm:AdNeBvprO0iJLhlaNu4op0+nUk1eZUzouT9jxX4pqA==]",
        "Db:Password": "ENC[AesGcm:Ey/1+CpyxSSMF6lDVqnsFs5XB7HK5X2hDGG1VOijJpt/r1MPzg==]",
        "Db:Port": "ENC[AesGcm:x9SCfZJaNlmtjKORr0z9L9zdx1qyQvLNQmvZefqP984=]"
      },
      "_stashlock": {
        "vaultKey": "v1vec.dev.00001",
        "mode": "sops",
        "dataKey": "onlnQvIWSg/O6DEHqxLS9A3sKz1BjrYLJIBBjjMwjyhCYLT1+5WE/eydpGtjS/nQUmSXnYgijM2ZBGv7zD/aiKoGZwUFsBQW/9bFT7Jn7wiRIHENFygBa+6XTuw=",
        "mac": "ENC[AesGcm:WrL1mdJIbTOUyEM9QFIJe+DfQW7xcIs0lc/CxYQp+rOR00pnPIJER+UPIX2rCxXxLrJukcMSX4BL94F2JBGrfwtubQiI+eHU]"
      }
    }
    """;
    private const string V1PrivateKey = "qFfz/7t6Q1fPT3n7gNpK+mdXnqVQdUqMN3wF6lGcelM=";

    private static readonly Dictionary<string, string> Secrets = new()
    {
        ["Db:Password"] = "s3cret",
        ["Db:Port"] = "5432",
        ["ApiKey"] = "abc"
    };

    private static (byte[] Public, byte[] Private) Keys() => Ecies.GenerateKeyPair();

    private static string Json(SopsEncryptedFile f) => JsonSerializer.Serialize(f);

    [Fact]
    public void V1Vector_StillDecrypts()
    {
        var result = SopsDecryptor.DecryptAutoDetect(V1File, Convert.FromBase64String(V1PrivateKey), "v1vec.dev.00001");
        Assert.Equal("v1-secret", result["Db:Password"]);
        Assert.Equal("5432", result["Db:Port"]);
        Assert.Equal("abc", result["ApiKey"]);
    }

    [Theory]
    [InlineData(1, "sops")]
    [InlineData(2, "sops-v2")]
    public void RoundTrip_BothFormats(int format, string mode)
    {
        var (pub, priv) = Keys();
        var file = SopsCodec.Encrypt(Secrets, pub, "app.dev.00001", format);
        Assert.Equal(mode, file.Metadata.Mode);
        var result = SopsDecryptor.DecryptAutoDetect(Json(file), priv, "app.dev.00001");
        Assert.Equal(Secrets.OrderBy(k => k.Key), result.OrderBy(k => k.Key));
    }

    [Fact]
    public void V2_ValueMovedToAnotherKey_IsRejected()
    {
        var (pub, priv) = Keys();
        var file = SopsCodec.Encrypt(Secrets, pub, "app.dev.00001");
        (file.Values["ApiKey"], file.Values["Db:Password"]) = (file.Values["Db:Password"], file.Values["ApiKey"]);
        Assert.Throws<CryptographicException>(() => SopsCodec.Decrypt(file, priv));
    }

    [Fact]
    public void V2_RemovedOrAddedKey_FailsMac()
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
    public void V2_VaultKeyIsBound()
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
    public void V2_DowngradeToV1Mode_IsRejected()
    {
        var (pub, priv) = Keys();
        var file = SopsCodec.Encrypt(Secrets, pub, "app.dev.00001");
        file.Metadata.Mode = SopsCodec.ModeV1;
        Assert.Throws<CryptographicException>(() => SopsCodec.Decrypt(file, priv));
    }

    [Fact]
    public void V2_WrongPrivateKey_IsReported()
    {
        var (pub, _) = Keys();
        var (_, otherPriv) = Keys();
        var file = SopsCodec.Encrypt(Secrets, pub, "app.dev.00001");
        Assert.Contains("wrong private key", Assert.Throws<CryptographicException>(() => SopsCodec.Decrypt(file, otherPriv)).Message);
    }

    [Fact]
    public void V2_IsNotReadableAsV1()
    {
        // Older clients only accept mode "sops"; a v2 file must not be mistaken for it
        var (pub, _) = Keys();
        Assert.NotEqual("sops", SopsCodec.Encrypt(Secrets, pub, "app.dev.00001").Metadata.Mode);
    }
}

public class CacheV2Tests : IDisposable
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
