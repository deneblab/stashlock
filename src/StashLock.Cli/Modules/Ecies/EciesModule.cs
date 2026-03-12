using System;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Deneblab.StashLock.Cli.Modules.Ecies;

/// <summary>
///     ECIES encryption/decryption using X25519 key agreement (BouncyCastle) + HKDF-SHA256 + AES-256-GCM (native .NET).
///     Seal: ephemeral X25519 keypair → key agreement → HKDF → AES-256-GCM encrypt
///     Output format: ephemeral_pubkey (32 bytes) || nonce (12 bytes) || ciphertext || tag (16 bytes)
/// </summary>
public static class EciesModule
{
    private const int PublicKeySize = 32;
    private const int PrivateKeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MinSealedSize = PublicKeySize + NonceSize + TagSize;

    /// <summary>
    ///     Generates a new X25519 keypair via BouncyCastle.
    /// </summary>
    public static KeyPairModel GenerateKeyPair(string tag)
    {
        var keyGen = new X25519KeyPairGenerator();
        keyGen.Init(new X25519KeyGenerationParameters(new SecureRandom()));
        var keyPair = keyGen.GenerateKeyPair();

        var publicKeyParams = (X25519PublicKeyParameters)keyPair.Public;
        var privateKeyParams = (X25519PrivateKeyParameters)keyPair.Private;

        var publicKeyBytes = publicKeyParams.GetEncoded();
        var privateKeyBytes = privateKeyParams.GetEncoded();

        return new KeyPairModel
        {
            Tag = tag,
            PublicKey = Convert.ToBase64String(publicKeyBytes),
            PrivateKey = Convert.ToBase64String(privateKeyBytes),
            CreatedAt = DateTime.UtcNow.ToString("o")
        };
    }

    /// <summary>
    ///     Encrypts plaintext using the recipient's X25519 public key.
    ///     Returns: ephemeral_pubkey (32) || nonce (12) || ciphertext || tag (16)
    /// </summary>
    public static byte[] Seal(byte[] plaintext, byte[] recipientPublicKeyBytes)
    {
        if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
        if (recipientPublicKeyBytes == null) throw new ArgumentNullException(nameof(recipientPublicKeyBytes));
        if (recipientPublicKeyBytes.Length != PublicKeySize)
            throw new ArgumentException($"Public key must be {PublicKeySize} bytes", nameof(recipientPublicKeyBytes));

        // Generate ephemeral X25519 keypair
        var keyGen = new X25519KeyPairGenerator();
        keyGen.Init(new X25519KeyGenerationParameters(new SecureRandom()));
        var ephemeralKeyPair = keyGen.GenerateKeyPair();

        var ephemeralPublic = (X25519PublicKeyParameters)ephemeralKeyPair.Public;
        var ephemeralPrivate = (X25519PrivateKeyParameters)ephemeralKeyPair.Private;
        var ephemeralPubBytes = ephemeralPublic.GetEncoded();

        // X25519 key agreement
        var recipientPubKey = new X25519PublicKeyParameters(recipientPublicKeyBytes);
        var agreement = new X25519Agreement();
        agreement.Init(ephemeralPrivate);
        var sharedSecret = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(recipientPubKey, sharedSecret, 0);

        // Derive AES key via HKDF-SHA256
        var aesKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret,
            32,
            salt: ephemeralPubBytes,
            info: System.Text.Encoding.UTF8.GetBytes("stashlock-ecies-v1"));

        // Encrypt with AES-256-GCM
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aesGcm = new AesGcm(aesKey, TagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        // Assemble: ephPubKey || nonce || ciphertext || tag
        var result = new byte[PublicKeySize + NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(ephemeralPubBytes, 0, result, 0, PublicKeySize);
        Buffer.BlockCopy(nonce, 0, result, PublicKeySize, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, result, PublicKeySize + NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, PublicKeySize + NonceSize + ciphertext.Length, TagSize);

        // Clear sensitive material
        CryptographicOperations.ZeroMemory(sharedSecret);
        CryptographicOperations.ZeroMemory(aesKey);

        return result;
    }

    /// <summary>
    ///     Decrypts a sealed message using the recipient's X25519 private key.
    /// </summary>
    public static byte[] Open(byte[] sealedMessage, byte[] recipientPrivateKeyBytes)
    {
        if (sealedMessage == null) throw new ArgumentNullException(nameof(sealedMessage));
        if (recipientPrivateKeyBytes == null) throw new ArgumentNullException(nameof(recipientPrivateKeyBytes));
        if (sealedMessage.Length < MinSealedSize)
            throw new InvalidOperationException(
                $"Sealed message too short: {sealedMessage.Length} bytes (minimum {MinSealedSize})");
        if (recipientPrivateKeyBytes.Length != PrivateKeySize)
            throw new ArgumentException($"Private key must be {PrivateKeySize} bytes", nameof(recipientPrivateKeyBytes));

        // Parse components
        var ephemeralPubBytes = sealedMessage[..PublicKeySize];
        var nonce = sealedMessage[PublicKeySize..(PublicKeySize + NonceSize)];
        var ciphertextLength = sealedMessage.Length - PublicKeySize - NonceSize - TagSize;
        var ciphertext = sealedMessage[(PublicKeySize + NonceSize)..(PublicKeySize + NonceSize + ciphertextLength)];
        var tag = sealedMessage[(PublicKeySize + NonceSize + ciphertextLength)..];

        // X25519 key agreement
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
            throw new InvalidOperationException(
                "Decryption failed — the sealed message could not be authenticated. " +
                "The private key may be wrong or the ciphertext is corrupted.");
        }

        // Clear sensitive material
        CryptographicOperations.ZeroMemory(sharedSecret);
        CryptographicOperations.ZeroMemory(aesKey);

        return plaintext;
    }
}
