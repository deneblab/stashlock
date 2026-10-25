#nullable enable
// Shared source: compiled into StashLock.Cli and StashLock.Client (see StashLock.Shared/README.md)
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Deneblab.StashLock.Shared.Crypto;

namespace Deneblab.StashLock.Shared.Formats;

/// <summary>
///     The stashlock.enc.*.json format ("mode": "sops"): every value is encrypted separately under a random
///     data key that is sealed with ECIES for the tag's public key. HKDF derives separate encryption and MAC keys
///     from the data key; each value is bound to its key path (AES-GCM associated data); a length-prefixed HMAC
///     covers all values and the vault key.
/// </summary>
internal static class SopsCodec
{
    public const string Mode = "sops";

    public static SopsEncryptedFile Encrypt(IReadOnlyDictionary<string, string> secrets, byte[] recipientPublicKey, string vaultKey)
    {
        var dataKey = AesGcmHelper.GenerateDataKey();
        var (encKey, macKey) = DeriveKeys(dataKey);
        try
        {
            var encrypted = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var kvp in secrets)
                encrypted[kvp.Key] = AesGcmHelper.EncryptValue(encKey, kvp.Value, associatedData: kvp.Key);

            return new SopsEncryptedFile
            {
                Values = new Dictionary<string, string>(encrypted),
                Metadata = new SopsMetadata
                {
                    VaultKey = vaultKey,
                    Mode = Mode,
                    DataKey = Convert.ToBase64String(Ecies.Seal(dataKey, recipientPublicKey)),
                    Mac = Convert.ToBase64String(AesGcmHelper.ComputeMac(macKey, vaultKey, encrypted))
                }
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(encKey);
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    /// <summary>
    ///     Decrypts a file. Throws <see cref="CryptographicException"/> with a readable message when the key is wrong,
    ///     the file was modified, the format is not supported, or (when given) it belongs to another vault key.
    /// </summary>
    public static Dictionary<string, string> Decrypt(SopsEncryptedFile file, byte[] privateKey, string? expectedVaultKey = null)
    {
        if (file?.Metadata == null || file.Values == null)
            throw new CryptographicException("Not a StashLock encrypted file: missing values or _stashlock metadata");
        if (file.Metadata.Mode != Mode)
            throw new CryptographicException($"Unsupported file format '{file.Metadata.Mode}' (expected '{Mode}'). Re-encode it with the current stashlock CLI.");
        if (expectedVaultKey != null && !string.Equals(file.Metadata.VaultKey, expectedVaultKey, StringComparison.Ordinal))
            throw new CryptographicException($"File is for '{file.Metadata.VaultKey}', expected '{expectedVaultKey}'");

        byte[] dataKey;
        try
        {
            dataKey = Ecies.Open(Convert.FromBase64String(file.Metadata.DataKey), privateKey);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new CryptographicException("Failed to unwrap the data key — wrong private key for this tag?", ex);
        }

        var (encKey, macKey) = DeriveKeys(dataKey);
        try
        {
            var sorted = new SortedDictionary<string, string>(file.Values, StringComparer.Ordinal);
            var expected = AesGcmHelper.ComputeMac(macKey, file.Metadata.VaultKey, sorted);
            if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(file.Metadata.Mac)))
                throw new CryptographicException("MAC verification failed — the file was modified");

            var result = new Dictionary<string, string>();
            foreach (var kvp in file.Values)
                result[kvp.Key] = AesGcmHelper.DecryptValue(encKey, kvp.Value, associatedData: kvp.Key);
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
            CryptographicOperations.ZeroMemory(encKey);
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    private static (byte[] EncKey, byte[] MacKey) DeriveKeys(byte[] dataKey) => (
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, salt: Array.Empty<byte>(), info: Encoding.UTF8.GetBytes("stashlock-sops-enc")),
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, salt: Array.Empty<byte>(), info: Encoding.UTF8.GetBytes("stashlock-sops-mac")));
}
