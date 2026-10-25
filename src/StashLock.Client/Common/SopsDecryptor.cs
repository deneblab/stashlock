using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deneblab.StashLock.Client.Common.Exceptions;
using Deneblab.StashLock.Shared.Crypto;
using Deneblab.StashLock.Shared.Formats;

namespace Deneblab.StashLock.Client.Common;

/// <summary>
///     High-level decryptor for SOPS-mode and whole-file-mode encrypted files.
///     Handles data key unwrapping, MAC verification, per-value decryption, and key unflattening.
/// </summary>
internal static class SopsDecryptor
{
    /// <summary>
    ///     Decrypts a SOPS-mode encrypted file (v1 "sops" or v2 "sops-v2") to a flat dictionary of plaintext values.
    ///     For v2 files, <paramref name="expectedVaultKey"/> (box.tag.version) is checked against the file.
    /// </summary>
    public static Dictionary<string, string> DecryptSops(SopsEncryptedFile sopsFile, byte[] privateKeyBytes, string expectedVaultKey = null)
    {
        if (sopsFile == null) throw new ArgumentNullException(nameof(sopsFile));
        if (privateKeyBytes == null) throw new ArgumentNullException(nameof(privateKeyBytes));
        try
        {
            return SopsCodec.Decrypt(sopsFile, privateKeyBytes, expectedVaultKey);
        }
        catch (CryptographicException ex)
        {
            throw new DecryptionException(ex.Message, ex);
        }
    }

    /// <summary>
    ///     Decrypts a whole-file mode encrypted file to plaintext JSON string.
    /// </summary>
    public static string DecryptWholeFile(StashlockEncryptedFile encFile, byte[] privateKeyBytes)
    {
        if (encFile == null) throw new ArgumentNullException(nameof(encFile));
        if (privateKeyBytes == null) throw new ArgumentNullException(nameof(privateKeyBytes));

        var sealedBytes = Convert.FromBase64String(encFile.EncryptedData);
        var plaintext = EciesDecryptor.Open(sealedBytes, privateKeyBytes);
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    ///     Auto-detects the encryption mode from a JSON string and decrypts accordingly.
    ///     Returns a flat Dictionary&lt;string, string&gt; with colon-separated keys for nested values.
    /// </summary>
    public static Dictionary<string, string> DecryptAutoDetect(string json, byte[] privateKeyBytes, string expectedVaultKey = null)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentNullException(nameof(json));

        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var root = doc.RootElement;

        // SOPS mode: has "_stashlock" metadata with mode "sops" (v1) or "sops-v2"
        if (root.TryGetProperty("_stashlock", out var metadata) &&
            metadata.TryGetProperty("mode", out var modeEl) &&
            SopsCodec.IsSops(modeEl.GetString()))
        {
            var sopsFile = JsonSerializer.Deserialize<SopsEncryptedFile>(json);
            if (sopsFile == null) throw new DecryptionException("Failed to parse SOPS encrypted file");

            return DecryptSops(sopsFile, privateKeyBytes, expectedVaultKey);
        }

        // Whole-file mode: has "VaultKey" and "EncryptedData"
        if (root.TryGetProperty("VaultKey", out _) && root.TryGetProperty("EncryptedData", out _))
        {
            var encFile = JsonSerializer.Deserialize<StashlockEncryptedFile>(json);
            if (encFile == null) throw new DecryptionException("Failed to parse whole-file encrypted file");

            var plaintextJson = DecryptWholeFile(encFile, privateKeyBytes);

            // Flatten the decrypted JSON to a dictionary
            return FlattenJson(plaintextJson);
        }

        throw new DecryptionException(
            "Unrecognized encrypted file format — expected SOPS mode (with _stashlock metadata) or whole-file mode (with VaultKey/EncryptedData)");
    }

    /// <summary>
    ///     Flattens a JSON object to a dictionary with colon-separated keys.
    /// </summary>
    private static Dictionary<string, string> FlattenJson(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var result = new Dictionary<string, string>();
        FlattenElement(doc.RootElement, "", result);
        return result;
    }

    private static void FlattenElement(JsonElement element, string prefix, Dictionary<string, string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}:{prop.Name}";
                    FlattenElement(prop.Value, key, result);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var key = $"{prefix}:{index}";
                    FlattenElement(item, key, result);
                    index++;
                }
                break;

            default:
                result[prefix] = element.ToString();
                break;
        }
    }
}
