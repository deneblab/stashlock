using System;
using System.Security.Cryptography;
using System.Text;

namespace Deneblab.StashLock.Cli.Modules.Ecies;

/// <summary>
///     Helper for per-value AES-256-GCM encryption/decryption.
///     Format: ENC[AesGcm:base64(nonce(12) + ciphertext + tag(16))]
/// </summary>
public static class AesGcmHelper
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string Prefix = "ENC[AesGcm:";
    private const string Suffix = "]";

    /// <summary>
    ///     Encrypts a plaintext string value with AES-256-GCM.
    ///     Returns formatted string: ENC[AesGcm:base64(nonce+ciphertext+tag)]
    /// </summary>
    public static string EncryptValue(byte[] dataKey, string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aesGcm = new AesGcm(dataKey, TagSize);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // nonce || ciphertext || tag
        var packed = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, packed, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, packed, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, packed, NonceSize + ciphertext.Length, TagSize);

        return $"{Prefix}{Convert.ToBase64String(packed)}{Suffix}";
    }

    /// <summary>
    ///     Decrypts a value in ENC[AesGcm:...] format.
    ///     Returns the original plaintext string.
    /// </summary>
    public static string DecryptValue(byte[] dataKey, string encryptedValue)
    {
        if (!IsEncrypted(encryptedValue))
            throw new InvalidOperationException("Value is not in ENC[AesGcm:...] format");

        var base64 = encryptedValue[Prefix.Length..^Suffix.Length];
        var packed = Convert.FromBase64String(base64);

        if (packed.Length < NonceSize + TagSize)
            throw new InvalidOperationException("Encrypted value too short");

        var nonce = packed[..NonceSize];
        var ciphertextLength = packed.Length - NonceSize - TagSize;
        var ciphertext = packed[NonceSize..(NonceSize + ciphertextLength)];
        var tag = packed[(NonceSize + ciphertextLength)..];

        var plaintext = new byte[ciphertextLength];
        using var aesGcm = new AesGcm(dataKey, TagSize);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    ///     Generates a random 32-byte data key for AES-256.
    /// </summary>
    public static byte[] GenerateDataKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    /// <summary>
    ///     Checks if a string value is in ENC[AesGcm:...] format.
    /// </summary>
    public static bool IsEncrypted(string value)
    {
        return value != null && value.StartsWith(Prefix, StringComparison.Ordinal) && value.EndsWith(Suffix, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Computes HMAC-SHA256 over all key-value pairs for integrity verification.
    ///     Keys are sorted to ensure deterministic MAC regardless of JSON property order.
    /// </summary>
    public static byte[] ComputeMac(byte[] dataKey, System.Collections.Generic.SortedDictionary<string, string> encryptedValues)
    {
        using var hmac = new HMACSHA256(dataKey);
        foreach (var kvp in encryptedValues)
        {
            var keyBytes = Encoding.UTF8.GetBytes(kvp.Key);
            var valueBytes = Encoding.UTF8.GetBytes(kvp.Value);
            hmac.TransformBlock(keyBytes, 0, keyBytes.Length, null, 0);
            hmac.TransformBlock(valueBytes, 0, valueBytes.Length, null, 0);
        }
        hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return hmac.Hash!;
    }

    /// <summary>
    ///     Verifies the HMAC-SHA256 MAC over encrypted values.
    /// </summary>
    public static bool VerifyMac(byte[] dataKey, System.Collections.Generic.SortedDictionary<string, string> encryptedValues, byte[] expectedMac)
    {
        var computedMac = ComputeMac(dataKey, encryptedValues);
        return CryptographicOperations.FixedTimeEquals(computedMac, expectedMac);
    }
}
