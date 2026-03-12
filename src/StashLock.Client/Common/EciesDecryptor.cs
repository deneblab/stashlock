using System;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;

namespace Deneblab.StashLock.Client.Common;

/// <summary>
///     ECIES decryption using X25519 key agreement (BouncyCastle) + HKDF-SHA256 + AES-256-GCM (native .NET).
///     Matches StashLock.Cli's EciesModule.Open() format:
///     Input: ephemeral_pubkey (32 bytes) || nonce (12 bytes) || ciphertext || tag (16 bytes)
/// </summary>
public static class EciesDecryptor
{
    private const int PublicKeySize = 32;
    private const int PrivateKeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MinSealedSize = PublicKeySize + NonceSize + TagSize;

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

        if (sealedMessage.Length < MinSealedSize)
            throw new Exceptions.DecryptionException(
                $"Sealed message is too short: {sealedMessage.Length} bytes (minimum {MinSealedSize})");

        if (recipientPrivateKeyBytes.Length != PrivateKeySize)
            throw new ArgumentException($"Private key must be {PrivateKeySize} bytes", nameof(recipientPrivateKeyBytes));

        try
        {
            // Parse components
            var ephemeralPubBytes = sealedMessage[..PublicKeySize];
            var nonce = sealedMessage[PublicKeySize..(PublicKeySize + NonceSize)];
            var ciphertextLength = sealedMessage.Length - PublicKeySize - NonceSize - TagSize;
            var ciphertext = sealedMessage[(PublicKeySize + NonceSize)..(PublicKeySize + NonceSize + ciphertextLength)];
            var tag = sealedMessage[(PublicKeySize + NonceSize + ciphertextLength)..];

            // X25519 key agreement via BouncyCastle
            var ephemeralPubKey = new X25519PublicKeyParameters(ephemeralPubBytes);
            var recipientPrivKey = new X25519PrivateKeyParameters(recipientPrivateKeyBytes);
            var agreement = new X25519Agreement();
            agreement.Init(recipientPrivKey);
            var sharedSecret = new byte[agreement.AgreementSize];
            agreement.CalculateAgreement(ephemeralPubKey, sharedSecret, 0);

            // Derive AES key via HKDF-SHA256
            var aesKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                sharedSecret,
                32,
                salt: ephemeralPubBytes,
                info: System.Text.Encoding.UTF8.GetBytes("stashlock-ecies-v1"));

            // Decrypt with AES-256-GCM
            var plaintext = new byte[ciphertextLength];
            try
            {
                using var aesGcm = new AesGcm(aesKey, TagSize);
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
            }
            catch (CryptographicException)
            {
                throw new Exceptions.DecryptionException(
                    "Decryption failed — wrong private key or corrupted ciphertext.");
            }

            // Clear sensitive material
            CryptographicOperations.ZeroMemory(sharedSecret);
            CryptographicOperations.ZeroMemory(aesKey);

            return plaintext;
        }
        catch (Exceptions.DecryptionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Exceptions.DecryptionException(
                $"ECIES decryption failed: {ex.Message}", ex);
        }
    }
}
