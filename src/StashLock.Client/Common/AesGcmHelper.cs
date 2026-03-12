using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Deneblab.StashLock.Client.Common;

/// <summary>
///     Helper for per-value AES-256-GCM decryption and MAC verification.
///     Compatible with StashLock.Cli's AesGcmHelper format: ENC[AesGcm:base64(nonce(12) + ciphertext + tag(16))]
/// </summary>
internal static class AesGcmHelper
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string Prefix = "ENC[AesGcm:";
    private const string Suffix = "]";

    /// <summary>
    ///     Decrypts a value in ENC[AesGcm:...] format.
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
    ///     Checks if a string value is in ENC[AesGcm:...] format.
    /// </summary>
    public static bool IsEncrypted(string value)
    {
        return value != null
            && value.StartsWith(Prefix, StringComparison.Ordinal)
            && value.EndsWith(Suffix, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Computes HMAC-SHA256 over all key-value pairs for integrity verification.
    ///     Keys are sorted to ensure deterministic MAC regardless of JSON property order.
    /// </summary>
    public static byte[] ComputeMac(byte[] dataKey, SortedDictionary<string, string> encryptedValues)
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
        return hmac.Hash;
    }

    /// <summary>
    ///     Verifies the HMAC-SHA256 MAC over encrypted values.
    /// </summary>
    public static bool VerifyMac(byte[] dataKey, SortedDictionary<string, string> encryptedValues, byte[] expectedMac)
    {
        var computedMac = ComputeMac(dataKey, encryptedValues);
        return CryptographicOperations.FixedTimeEquals(computedMac, expectedMac);
    }
}
