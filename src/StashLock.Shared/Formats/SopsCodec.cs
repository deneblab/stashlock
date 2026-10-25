#nullable enable
// Shared source: compiled into StashLock.Cli and StashLock.Client (see StashLock.Shared/README.md)
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Deneblab.StashLock.Shared.Crypto;

namespace Deneblab.StashLock.Shared.Formats;

/// <summary>
///     Encrypts and decrypts SOPS-mode files (per-value encryption under one data key wrapped with ECIES).
///     v1 ("sops"): one key for AES-GCM and HMAC, MAC over concatenated fields.
///     v2 ("sops-v2"): HKDF-derived encryption and MAC keys, each value bound to its key path (AAD),
///     length-prefixed MAC that also covers the vault key.
/// </summary>
internal static class SopsCodec
{
    public const string ModeV1 = "sops";
    public const string ModeV2 = "sops-v2";

    public static bool IsSops(string? mode) => mode == ModeV1 || mode == ModeV2;

    public static SopsEncryptedFile Encrypt(IReadOnlyDictionary<string, string> secrets, byte[] recipientPublicKey, string vaultKey, int format = 2)
    {
        if (format != 1 && format != 2)
            throw new ArgumentOutOfRangeException(nameof(format), "SOPS format must be 1 or 2");

        var dataKey = AesGcmHelper.GenerateDataKey();
        try
        {
            var encrypted = new SortedDictionary<string, string>(StringComparer.Ordinal);
            string mac;
            if (format == 2)
            {
                var (encKey, macKey) = DeriveV2Keys(dataKey);
                try
                {
                    foreach (var kvp in secrets)
                        encrypted[kvp.Key] = AesGcmHelper.EncryptValue(encKey, kvp.Value, associatedData: kvp.Key);
                    mac = Convert.ToBase64String(AesGcmHelper.ComputeMacV2(macKey, vaultKey, encrypted));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encKey);
                    CryptographicOperations.ZeroMemory(macKey);
                }
            }
            else
            {
                foreach (var kvp in secrets)
                    encrypted[kvp.Key] = AesGcmHelper.EncryptValue(dataKey, kvp.Value);
                var v1Mac = AesGcmHelper.ComputeMac(dataKey, encrypted);
                mac = AesGcmHelper.EncryptValue(dataKey, Convert.ToBase64String(v1Mac));
            }

            return new SopsEncryptedFile
            {
                Values = new Dictionary<string, string>(encrypted),
                Metadata = new SopsMetadata
                {
                    VaultKey = vaultKey,
                    Mode = format == 2 ? ModeV2 : ModeV1,
                    DataKey = Convert.ToBase64String(Ecies.Seal(dataKey, recipientPublicKey)),
                    Mac = mac
                }
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>
    ///     Decrypts a v1 or v2 file. Throws <see cref="CryptographicException"/> with a readable message when the
    ///     key is wrong, the file was modified, or (v2, when given) the file belongs to another vault key.
    /// </summary>
    public static Dictionary<string, string> Decrypt(SopsEncryptedFile file, byte[] privateKey, string? expectedVaultKey = null)
    {
        if (file?.Metadata == null || file.Values == null)
            throw new CryptographicException("Not a SOPS-mode file: missing values or _stashlock metadata");
        if (!IsSops(file.Metadata.Mode))
            throw new CryptographicException($"Unsupported SOPS mode '{file.Metadata.Mode}' (expected '{ModeV1}' or '{ModeV2}')");

        byte[] dataKey;
        try
        {
            dataKey = Ecies.Open(Convert.FromBase64String(file.Metadata.DataKey), privateKey);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new CryptographicException("Failed to unwrap the data key — wrong private key for this tag?", ex);
        }

        try
        {
            var sorted = new SortedDictionary<string, string>(file.Values, StringComparer.Ordinal);
            var result = new Dictionary<string, string>();

            if (file.Metadata.Mode == ModeV2)
            {
                if (expectedVaultKey != null && !string.Equals(file.Metadata.VaultKey, expectedVaultKey, StringComparison.Ordinal))
                    throw new CryptographicException($"File is for '{file.Metadata.VaultKey}', expected '{expectedVaultKey}'");

                var (encKey, macKey) = DeriveV2Keys(dataKey);
                try
                {
                    var expected = AesGcmHelper.ComputeMacV2(macKey, file.Metadata.VaultKey, sorted);
                    if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(file.Metadata.Mac)))
                        throw new CryptographicException("MAC verification failed — the file was modified");
                    foreach (var kvp in file.Values)
                        result[kvp.Key] = AesGcmHelper.DecryptValue(encKey, kvp.Value, associatedData: kvp.Key);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encKey);
                    CryptographicOperations.ZeroMemory(macKey);
                }
            }
            else
            {
                var expectedMac = Convert.FromBase64String(AesGcmHelper.DecryptValue(dataKey, file.Metadata.Mac));
                if (!AesGcmHelper.VerifyMac(dataKey, sorted, expectedMac))
                    throw new CryptographicException("MAC verification failed — the file was modified");
                foreach (var kvp in file.Values)
                    result[kvp.Key] = AesGcmHelper.DecryptValue(dataKey, kvp.Value);
            }

            return result;
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new CryptographicException("A value failed authentication — it was modified or moved to another key", ex);
        }
        catch (Exception ex) when (ex is not CryptographicException)
        {
            throw new CryptographicException("Failed to decrypt a value — the file was modified or is corrupt: " + ex.Message, ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private static (byte[] EncKey, byte[] MacKey) DeriveV2Keys(byte[] dataKey) => (
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, salt: Array.Empty<byte>(), info: Encoding.UTF8.GetBytes("stashlock-sops-v2-enc")),
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, salt: Array.Empty<byte>(), info: Encoding.UTF8.GetBytes("stashlock-sops-v2-mac")));
}
