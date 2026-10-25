#nullable enable
// Shared source: compiled into StashLock.Cli and StashLock.Client (see StashLock.Shared/README.md)
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Deneblab.StashLock.Shared.Crypto;

/// <summary>
///     Helper for per-value AES-256-GCM encryption/decryption.
///     Format: ENC[AesGcm:base64(nonce(12) + ciphertext + tag(16))]
/// </summary>
internal static class AesGcmHelper
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string Prefix = "ENC[AesGcm:";
    private const string Suffix = "]";

    /// <summary>
    ///     Encrypts a plaintext string value with AES-256-GCM.
    ///     Returns formatted string: ENC[AesGcm:base64(nonce+ciphertext+tag)]
    /// </summary>
    public static string EncryptValue(byte[] dataKey, string plaintext, string? associatedData = null)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aesGcm = new AesGcm(dataKey, TagSize);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag, Aad(associatedData));

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
    public static string DecryptValue(byte[] dataKey, string encryptedValue, string? associatedData = null)
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
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, Aad(associatedData));

        return Encoding.UTF8.GetString(plaintext);
    }

    // Associated data is authenticated but not encrypted (the file format binds each value to its key path)
    private static byte[]? Aad(string? associatedData) =>
        associatedData == null ? null : Encoding.UTF8.GetBytes(associatedData);

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
    ///     File MAC: HMAC-SHA256 over a length-prefixed encoding of the format label, the vault key
    ///     and every (key, encrypted value) pair in ordinal order, so field boundaries are unambiguous.
    /// </summary>
    public static byte[] ComputeMac(byte[] macKey, string vaultKey, SortedDictionary<string, string> encryptedValues)
    {
        using var hmac = new HMACSHA256(macKey);
        void Field(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
            hmac.TransformBlock(length, 0, 4, null, 0);
            hmac.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        Field("stashlock-sops");
        Field(vaultKey);
        foreach (var kvp in encryptedValues)
        {
            Field(kvp.Key);
            Field(kvp.Value);
        }
        hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return hmac.Hash!;
    }
}
