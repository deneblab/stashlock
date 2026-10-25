using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deneblab.StashLock.Client.Common;

/// <summary>
/// Manages encrypted cache files for offline secret access.
/// Cache is machine-bound via HKDF key derivation from private key + machine identity.
/// Format SLC2: magic | createdAt | TTL | nonce | length | ciphertext | tag. The header and the
/// cache file name (stashlock.cache.box.tag.version.bin) are AES-GCM associated data, so changing
/// the TTL or copying the file over another entry's cache is detected. SLC1 files are ignored.
/// </summary>
internal static class SecretsCacheManager
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SLC2");
    private static readonly byte[] HkdfSalt = SHA256.HashData(Encoding.UTF8.GetBytes("stashlock-cache-v2"));
    private const int HeaderSize = 4 + 8 + 4 + NonceSize;

    // Header bytes + file name, authenticated by AES-GCM
    private static byte[] AssociatedData(byte[] header, string filePath)
    {
        var name = Encoding.UTF8.GetBytes(Path.GetFileName(filePath));
        var aad = new byte[header.Length + name.Length];
        Buffer.BlockCopy(header, 0, aad, 0, header.Length);
        Buffer.BlockCopy(name, 0, aad, header.Length, name.Length);
        return aad;
    }

    private static byte[] Header(long createdTicks, int ttlSeconds, byte[] nonce)
    {
        var header = new byte[HeaderSize];
        Buffer.BlockCopy(Magic, 0, header, 0, 4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(4, 8), createdTicks);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), ttlSeconds);
        Buffer.BlockCopy(nonce, 0, header, 16, NonceSize);
        return header;
    }

    /// <summary>
    /// Derives a machine-bound cache encryption key using HKDF-SHA256.
    /// </summary>
    internal static byte[] DeriveCacheKey(byte[] privateKey, string machineId = null)
    {
        var machine = machineId ?? Environment.MachineName;
        var user = Environment.UserName;
        var info = Encoding.UTF8.GetBytes($"{machine}:{user}");

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, privateKey, 32, HkdfSalt, info);
    }

    /// <summary>
    /// Writes an encrypted cache file using AES-256-GCM. Uses atomic write (temp + rename).
    /// </summary>
    internal static void WriteCache(string filePath, Dictionary<string, string> secrets, byte[] cacheKey, TimeSpan ttl, ILogger logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        var json = JsonSerializer.Serialize(secrets);
        var plaintext = Encoding.UTF8.GetBytes(json);

        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        var createdTicks = DateTimeOffset.UtcNow.UtcTicks;
        var ttlSeconds = (int)ttl.TotalSeconds;
        var header = Header(createdTicks, ttlSeconds, nonce);

        using (var aes = new AesGcm(cacheKey, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(header, filePath));
        }

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Atomic write: write to temp file, then rename
        var tempPath = filePath + ".tmp";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(header);                                   // 28 bytes: "SLC2" | CreatedAt | TTL | nonce
                bw.Write(ciphertext.Length);                        // 4 bytes: ciphertext length
                bw.Write(ciphertext);                               // N bytes: ciphertext
                bw.Write(tag);                                      // 16 bytes: auth tag
            }

            // Replace target atomically
            File.Move(tempPath, filePath, overwrite: true);
            log.LogDebug("Cache write {File} ({Count} entries, ttl {TtlSec}s)",
                Path.GetFileName(filePath), secrets.Count, (int)ttl.TotalSeconds);
        }
        finally
        {
            // Clean up temp file if rename failed
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>
    /// Reads and decrypts a cache file. Returns null if corrupted or missing.
    /// Also returns null when the entry has expired, unless <paramref name="ignoreTtl"/>
    /// is true (used by the ServerFirstOutdatedCacheOnError strategy to serve an
    /// outdated entry when the server is unreachable).
    /// </summary>
    internal static Dictionary<string, string> ReadCache(string filePath, byte[] cacheKey, bool ignoreTtl = false, ILogger logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        var file = Path.GetFileName(filePath);

        if (!File.Exists(filePath))
        {
            log.LogDebug("Cache miss {File}: not present", file);
            return null;
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            // Validate magic
            var magic = br.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] ||
                magic[2] != Magic[2] || magic[3] != Magic[3])
            {
                log.LogDebug("Cache miss {File}: unknown or old cache format (rebuilt on the next server fetch)", file);
                return null;
            }

            var createdTicks = br.ReadInt64();
            var ttlSeconds = br.ReadInt32();
            var nonce = br.ReadBytes(NonceSize);
            if (nonce.Length != NonceSize)
                return null;
            var header = Header(createdTicks, ttlSeconds, nonce);
            var ciphertextLength = br.ReadInt32();
            var ciphertext = br.ReadBytes(ciphertextLength);
            var tag = br.ReadBytes(TagSize);

            // Check TTL (skipped when the caller explicitly accepts an outdated entry)
            if (!ignoreTtl)
            {
                var created = new DateTimeOffset(createdTicks, TimeSpan.Zero);
                var expiry = created.AddSeconds(ttlSeconds);
                if (DateTimeOffset.UtcNow > expiry)
                {
                    log.LogDebug("Cache miss {File}: expired", file);
                    return null;
                }
            }

            // Validate sizes
            if (nonce.Length != NonceSize || tag.Length != TagSize || ciphertext.Length != ciphertextLength)
                return null;

            // Decrypt
            var plaintext = new byte[ciphertextLength];
            using (var aes = new AesGcm(cacheKey, TagSize))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(header, filePath));
            }

            var json = Encoding.UTF8.GetString(plaintext);
            var result = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            log.LogDebug("Cache hit {File} ({Count} entries{Outdated})",
                file, result?.Count ?? 0, ignoreTtl ? ", outdated-accepted" : "");
            return result;
        }
        catch
        {
            // Corrupted or wrong key — return null (no key/secret material in the log)
            log.LogWarning("Cache miss {File}: unreadable (corrupt or wrong key)", file);
            return null;
        }
    }

    /// <summary>
    /// Builds the cache file path from box.tag.version.
    /// </summary>
    internal static string GetCacheFilePath(string cacheDir, string box, string tag, string version)
    {
        return Path.Combine(cacheDir, $"stashlock.cache.{box}.{tag}.{version}.bin");
    }
}
