using System;
using System.Collections.Generic;
using Deneblab.StashLock.Cli.Modules.Ecies;
using Xunit;
using Deneblab.StashLock.Shared.Crypto;

namespace Deneblab.StashLock.Tests.Cli;

public class AesGcmHelperTests
{
    [Fact]
    public void GenerateDataKey_ShouldReturn32Bytes()
    {
        var key = AesGcmHelper.GenerateDataKey();
        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void GenerateDataKey_ShouldProduceUniqueKeys()
    {
        var key1 = AesGcmHelper.GenerateDataKey();
        var key2 = AesGcmHelper.GenerateDataKey();
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void EncryptDecrypt_ShouldRoundTrip()
    {
        var dataKey = AesGcmHelper.GenerateDataKey();
        var original = "hello world";

        var encrypted = AesGcmHelper.EncryptValue(dataKey, original);
        var decrypted = AesGcmHelper.DecryptValue(dataKey, encrypted);

        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_ShouldRoundTripWithUnicode()
    {
        var dataKey = AesGcmHelper.GenerateDataKey();
        var original = "héllo wörld! 日本語 🔐";

        var encrypted = AesGcmHelper.EncryptValue(dataKey, original);
        var decrypted = AesGcmHelper.DecryptValue(dataKey, encrypted);

        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_ShouldRoundTripWithEmptyString()
    {
        var dataKey = AesGcmHelper.GenerateDataKey();
        var original = "";

        var encrypted = AesGcmHelper.EncryptValue(dataKey, original);
        var decrypted = AesGcmHelper.DecryptValue(dataKey, encrypted);

        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void EncryptValue_ShouldProduceEncFormat()
    {
        var dataKey = AesGcmHelper.GenerateDataKey();
        var encrypted = AesGcmHelper.EncryptValue(dataKey, "test");

        Assert.StartsWith("ENC[AesGcm:", encrypted);
        Assert.EndsWith("]", encrypted);
    }

    [Fact]
    public void EncryptValue_ShouldProduceDifferentCiphertextEachTime()
    {
        var dataKey = AesGcmHelper.GenerateDataKey();
        var enc1 = AesGcmHelper.EncryptValue(dataKey, "same");
        var enc2 = AesGcmHelper.EncryptValue(dataKey, "same");

        Assert.NotEqual(enc1, enc2);
    }

    [Fact]
    public void IsEncrypted_ShouldDetectEncFormat()
    {
        Assert.True(AesGcmHelper.IsEncrypted("ENC[AesGcm:abc123==]"));
        Assert.False(AesGcmHelper.IsEncrypted("plaintext"));
        Assert.False(AesGcmHelper.IsEncrypted("ENC[other:abc]"));
        Assert.False(AesGcmHelper.IsEncrypted(""));
        Assert.False(AesGcmHelper.IsEncrypted(null!));
    }

    [Fact]
    public void DecryptValue_ShouldFailWithWrongKey()
    {
        var key1 = AesGcmHelper.GenerateDataKey();
        var key2 = AesGcmHelper.GenerateDataKey();

        var encrypted = AesGcmHelper.EncryptValue(key1, "secret");

        Assert.ThrowsAny<Exception>(
            () => AesGcmHelper.DecryptValue(key2, encrypted));
    }

    [Fact]
    public void DecryptValue_ShouldThrowOnNonEncFormat()
    {
        var dataKey = AesGcmHelper.GenerateDataKey();

        Assert.Throws<InvalidOperationException>(
            () => AesGcmHelper.DecryptValue(dataKey, "plaintext"));
    }

    [Fact]
    public void ComputeMac_IsDeterministicAndCoversKeysValuesAndVaultKey()
    {
        var key = AesGcmHelper.GenerateDataKey();
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["a"] = "1", ["b"] = "2" };
        var mac = AesGcmHelper.ComputeMac(key, "app.dev.00001", values);

        Assert.Equal(mac, AesGcmHelper.ComputeMac(key, "app.dev.00001", values));
        Assert.NotEqual(mac, AesGcmHelper.ComputeMac(key, "app.prod.00001", values));
        Assert.NotEqual(mac, AesGcmHelper.ComputeMac(AesGcmHelper.GenerateDataKey(), "app.dev.00001", values));

        var changed = new SortedDictionary<string, string>(values, StringComparer.Ordinal) { ["b"] = "3" };
        Assert.NotEqual(mac, AesGcmHelper.ComputeMac(key, "app.dev.00001", changed));
    }

    [Fact]
    public void ComputeMac_FieldBoundariesMatter()
    {
        // Without length prefixes ("ab","c") and ("a","bc") would hash the same bytes
        var key = AesGcmHelper.GenerateDataKey();
        var one = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["ab"] = "c" };
        var two = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["a"] = "bc" };
        Assert.NotEqual(AesGcmHelper.ComputeMac(key, "v", one), AesGcmHelper.ComputeMac(key, "v", two));
    }

    [Fact]
    public void EncryptDecrypt_WithAssociatedData_MustMatch()
    {
        var key = AesGcmHelper.GenerateDataKey();
        var enc = AesGcmHelper.EncryptValue(key, "secret", associatedData: "Db:Password");
        Assert.Equal("secret", AesGcmHelper.DecryptValue(key, enc, associatedData: "Db:Password"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => AesGcmHelper.DecryptValue(key, enc, associatedData: "ApiKey"));
    }
}
