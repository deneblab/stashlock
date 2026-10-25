using System;

namespace Deneblab.StashLock.Client.Common;

/// <summary>
///     ECIES decryption using X25519 key agreement (BouncyCastle) + HKDF-SHA256 + AES-256-GCM (native .NET).
///     Public wrapper over StashLock.Shared (Crypto/Ecies.cs), the same code the CLI uses:
///     Input: ephemeral_pubkey (32 bytes) || nonce (12 bytes) || ciphertext || tag (16 bytes)
/// </summary>
public static class EciesDecryptor
{
    /// <summary>
    ///     Decrypts a sealed message using the recipient's X25519 private key.
    /// </summary>
    /// <param name="sealedMessage">The sealed message bytes (ephPubKey || nonce || ciphertext || tag)</param>
    /// <param name="recipientPrivateKeyBytes">The X25519 private key (32 bytes)</param>
    /// <returns>Decrypted plaintext bytes</returns>
    /// <exception cref="Exceptions.DecryptionException">Thrown when decryption fails</exception>
    public static byte[] Open(byte[] sealedMessage, byte[] recipientPrivateKeyBytes)
    {
        if (sealedMessage == null) throw new ArgumentNullException(nameof(sealedMessage));
        if (recipientPrivateKeyBytes == null) throw new ArgumentNullException(nameof(recipientPrivateKeyBytes));
        try
        {
            return Shared.Crypto.Ecies.Open(sealedMessage, recipientPrivateKeyBytes);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Exceptions.DecryptionException(
                "Decryption failed — the private key may be wrong or the data is corrupted: " + ex.Message, ex);
        }
    }
}
