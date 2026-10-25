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
///     Decrypts stashlock.enc.*.json files (see StashLock.Shared/Formats/SopsCodec.cs) to flat
///     colon-separated keys.
/// </summary>
internal static class SopsDecryptor
{
    /// <summary>
    ///     Decrypts a parsed file. When <paramref name="expectedVaultKey"/> (box.tag.version) is given,
    ///     the file must belong to it.
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
    ///     Parses and decrypts file content (as returned by the server or read from disk).
    /// </summary>
    public static Dictionary<string, string> DecryptAutoDetect(string json, byte[] privateKeyBytes, string expectedVaultKey = null)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentNullException(nameof(json));

        SopsEncryptedFile file;
        try
        {
            file = JsonSerializer.Deserialize<SopsEncryptedFile>(json, new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException ex)
        {
            throw new DecryptionException("Not a StashLock encrypted file (invalid JSON): " + ex.Message, ex);
        }

        if (file?.Metadata == null || string.IsNullOrEmpty(file.Metadata.Mode))
            throw new DecryptionException("Not a StashLock encrypted file: no _stashlock metadata. Encode it with the current stashlock CLI.");

        return DecryptSops(file, privateKeyBytes, expectedVaultKey);
    }
}
