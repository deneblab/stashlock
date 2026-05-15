using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Deneblab.StashLock.Client.Common;

/// <summary>
/// Manages encrypted cache files for offline secret access.
/// Cache is machine-bound via HKDF key derivation from private key + machine identity.
/// </summary>
internal static class SecretsCacheManager
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SLC1");
    private static readonly byte[] HkdfSalt = SHA256.HashData(Encoding.UTF8.GetBytes("stashlock-cache-v1"));

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
    internal static void WriteCache(string filePath, Dictionary<string, string> secrets, byte[] cacheKey, TimeSpan ttl)
    {
        var json = JsonSerializer.Serialize(secrets);
        var plaintext = Encoding.UTF8.GetBytes(json);

        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(cacheKey, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
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
                bw.Write(Magic);                                    // 4 bytes: "SLC1"
                bw.Write(DateTimeOffset.UtcNow.UtcTicks);          // 8 bytes: CreatedAt
                bw.Write((int)ttl.TotalSeconds);                   // 4 bytes: TTL seconds
                bw.Write(nonce);                                    // 12 bytes: nonce
                bw.Write(ciphertext.Length);                        // 4 bytes: ciphertext length
                bw.Write(ciphertext);                               // N bytes: ciphertext
                bw.Write(tag);                                      // 16 bytes: auth tag
            }

            // Replace target atomically
            File.Move(tempPath, filePath, overwrite: true);
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
    internal static Dictionary<string, string> ReadCache(string filePath, byte[] cacheKey, bool ignoreTtl = false)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            // Validate magic
            var magic = br.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] ||
                magic[2] != Magic[2] || magic[3] != Magic[3])
                return null;

            var createdTicks = br.ReadInt64();
            var ttlSeconds = br.ReadInt32();
            var nonce = br.ReadBytes(NonceSize);
            var ciphertextLength = br.ReadInt32();
            var ciphertext = br.ReadBytes(ciphertextLength);
            var tag = br.ReadBytes(TagSize);

            // Check TTL (skipped when the caller explicitly accepts an outdated entry)
            if (!ignoreTtl)
            {
                var created = new DateTimeOffset(createdTicks, TimeSpan.Zero);
                var expiry = created.AddSeconds(ttlSeconds);
                if (DateTimeOffset.UtcNow > expiry)
                    return null;
            }

            // Validate sizes
            if (nonce.Length != NonceSize || tag.Length != TagSize || ciphertext.Length != ciphertextLength)
                return null;

            // Decrypt
            var plaintext = new byte[ciphertextLength];
            using (var aes = new AesGcm(cacheKey, TagSize))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            var json = Encoding.UTF8.GetString(plaintext);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch
        {
            // Corrupted or wrong key — silently return null
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
