using System;
using Deneblab.StashLock.Shared.Crypto;

namespace Deneblab.StashLock.Cli.Modules.Ecies;

/// <summary>
///     CLI entry points for X25519 ECIES; the algorithm lives in StashLock.Shared (Crypto/Ecies.cs).
/// </summary>
public static class EciesModule
{
    /// <summary>
    ///     Generates a new X25519 key pair for a tag, as stored in stashlock.key.{tag}.json.
    /// </summary>
    public static KeyPairModel GenerateKeyPair(string tag)
    {
        var (publicKey, privateKey) = Shared.Crypto.Ecies.GenerateKeyPair();
        return new KeyPairModel
        {
            Tag = tag,
            PublicKey = Convert.ToBase64String(publicKey),
            PrivateKey = Convert.ToBase64String(privateKey),
            CreatedAt = DateTime.UtcNow.ToString("o")
        };
    }

    /// <summary>Encrypts plaintext for the recipient's public key: ephPubKey (32) || nonce (12) || ciphertext || tag (16).</summary>
    public static byte[] Seal(byte[] plaintext, byte[] recipientPublicKeyBytes) =>
        Shared.Crypto.Ecies.Seal(plaintext, recipientPublicKeyBytes);

    /// <summary>Decrypts a sealed message with the recipient's private key.</summary>
    public static byte[] Open(byte[] sealedMessage, byte[] recipientPrivateKeyBytes) =>
        Shared.Crypto.Ecies.Open(sealedMessage, recipientPrivateKeyBytes);
}
