using System;
using System.Text;
using Deneblab.StashLock.Cli.Modules.Ecies;
using Xunit;

namespace Deneblab.StashLock.Tests.Cli;

public class EciesModuleTests
{
    [Fact]
    public void GenerateKeyPair_ShouldReturnValidKeyPair()
    {
        var keyPair = EciesModule.GenerateKeyPair("test-tag");

        Assert.Equal("test-tag", keyPair.Tag);
        Assert.NotEmpty(keyPair.PublicKey);
        Assert.NotEmpty(keyPair.PrivateKey);
        Assert.NotEmpty(keyPair.CreatedAt);

        var pubBytes = Convert.FromBase64String(keyPair.PublicKey);
        var privBytes = Convert.FromBase64String(keyPair.PrivateKey);
        Assert.Equal(32, pubBytes.Length);
        Assert.Equal(32, privBytes.Length);
    }

    [Fact]
    public void GenerateKeyPair_ShouldProduceUniqueKeys()
    {
        var kp1 = EciesModule.GenerateKeyPair("tag1");
        var kp2 = EciesModule.GenerateKeyPair("tag2");

        Assert.NotEqual(kp1.PublicKey, kp2.PublicKey);
        Assert.NotEqual(kp1.PrivateKey, kp2.PrivateKey);
    }

    [Fact]
    public void SealOpen_ShouldRoundTrip()
    {
        var keyPair = EciesModule.GenerateKeyPair("roundtrip");
        var plaintext = Encoding.UTF8.GetBytes("hello ecies");

        var sealed_ = EciesModule.Seal(plaintext, keyPair.GetPublicKeyBytes());
        var opened = EciesModule.Open(sealed_, keyPair.GetPrivateKeyBytes());

        Assert.Equal(plaintext, opened);
    }

    [Fact]
    public void SealOpen_ShouldRoundTripWithJsonContent()
    {
        var keyPair = EciesModule.GenerateKeyPair("json");
        var json = "{\"db\":\"Server=prod;Password=s3cret\",\"api_key\":\"abc123\"}";
        var plaintext = Encoding.UTF8.GetBytes(json);

        var sealed_ = EciesModule.Seal(plaintext, keyPair.GetPublicKeyBytes());
        var opened = EciesModule.Open(sealed_, keyPair.GetPrivateKeyBytes());

        Assert.Equal(json, Encoding.UTF8.GetString(opened));
    }

    [Fact]
    public void SealOpen_ShouldRoundTripWithEmptyPlaintext()
    {
        var keyPair = EciesModule.GenerateKeyPair("empty");
        var plaintext = Array.Empty<byte>();

        var sealed_ = EciesModule.Seal(plaintext, keyPair.GetPublicKeyBytes());
        var opened = EciesModule.Open(sealed_, keyPair.GetPrivateKeyBytes());

        Assert.Empty(opened);
    }

    [Fact]
    public void SealOpen_ShouldRoundTripWithLargePlaintext()
    {
        var keyPair = EciesModule.GenerateKeyPair("large");
        var plaintext = new byte[50_000];
        new Random(42).NextBytes(plaintext);

        var sealed_ = EciesModule.Seal(plaintext, keyPair.GetPublicKeyBytes());
        var opened = EciesModule.Open(sealed_, keyPair.GetPrivateKeyBytes());

        Assert.Equal(plaintext, opened);
    }

    [Fact]
    public void Seal_OutputShouldContainEphemeralPublicKeyAndNonce()
    {
        var keyPair = EciesModule.GenerateKeyPair("format");
        var plaintext = Encoding.UTF8.GetBytes("test");

        var sealed_ = EciesModule.Seal(plaintext, keyPair.GetPublicKeyBytes());

        // Output: eph_pubkey(32) + nonce(12) + ciphertext + tag(16)
        Assert.True(sealed_.Length >= 32 + 12 + 16 + plaintext.Length);
    }

    [Fact]
    public void Seal_ShouldProduceDifferentCiphertextEachTime()
    {
        var keyPair = EciesModule.GenerateKeyPair("nondeterministic");
        var plaintext = Encoding.UTF8.GetBytes("same input");

        var sealed1 = EciesModule.Seal(plaintext, keyPair.GetPublicKeyBytes());
        var sealed2 = EciesModule.Seal(plaintext, keyPair.GetPublicKeyBytes());

        Assert.NotEqual(sealed1, sealed2);
    }

    [Fact]
    public void Open_ShouldFailWithWrongPrivateKey()
    {
        var kpSender = EciesModule.GenerateKeyPair("sender");
        var kpWrong = EciesModule.GenerateKeyPair("wrong");
        var plaintext = Encoding.UTF8.GetBytes("secret data");

        var sealed_ = EciesModule.Seal(plaintext, kpSender.GetPublicKeyBytes());

        Assert.Throws<InvalidOperationException>(
            () => EciesModule.Open(sealed_, kpWrong.GetPrivateKeyBytes()));
    }

    [Fact]
    public void Open_ShouldFailWithCorruptedCiphertext()
    {
        var keyPair = EciesModule.GenerateKeyPair("corrupt");
        var plaintext = Encoding.UTF8.GetBytes("integrity check");

        var sealed_ = EciesModule.Seal(plaintext, keyPair.GetPublicKeyBytes());

        // Corrupt a byte in the ciphertext area (after 32-byte pubkey + 12-byte nonce)
        sealed_[50] ^= 0xFF;

        Assert.Throws<InvalidOperationException>(
            () => EciesModule.Open(sealed_, keyPair.GetPrivateKeyBytes()));
    }

    [Fact]
    public void Open_ShouldFailWithTooShortMessage()
    {
        var keyPair = EciesModule.GenerateKeyPair("short");
        var tooShort = new byte[10]; // Less than 32 + 12 + 16 = 60 minimum

        Assert.Throws<InvalidOperationException>(
            () => EciesModule.Open(tooShort, keyPair.GetPrivateKeyBytes()));
    }

    [Fact]
    public void Seal_ShouldThrowOnNullPlaintext()
    {
        var keyPair = EciesModule.GenerateKeyPair("null");
        Assert.Throws<ArgumentNullException>(
            () => EciesModule.Seal(null!, keyPair.GetPublicKeyBytes()));
    }

    [Fact]
    public void Seal_ShouldThrowOnNullPublicKey()
    {
        Assert.Throws<ArgumentNullException>(
            () => EciesModule.Seal(new byte[] { 1, 2, 3 }, null!));
    }

    [Fact]
    public void Open_ShouldThrowOnNullSealedMessage()
    {
        var keyPair = EciesModule.GenerateKeyPair("null");
        Assert.Throws<ArgumentNullException>(
            () => EciesModule.Open(null!, keyPair.GetPrivateKeyBytes()));
    }

    [Fact]
    public void Open_ShouldThrowOnNullPrivateKey()
    {
        Assert.Throws<ArgumentNullException>(
            () => EciesModule.Open(new byte[100], null!));
    }

    [Fact]
    public void SealOpen_ShouldRoundTripWithUnicodeContent()
    {
        var keyPair = EciesModule.GenerateKeyPair("unicode");
        var text = "héllo wörld! @#$%^&*() 日本語 🔐";
        var plaintext = Encoding.UTF8.GetBytes(text);

        var sealed_ = EciesModule.Seal(plaintext, keyPair.GetPublicKeyBytes());
        var opened = EciesModule.Open(sealed_, keyPair.GetPrivateKeyBytes());

        Assert.Equal(text, Encoding.UTF8.GetString(opened));
    }
}
