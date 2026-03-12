using System;
using System.Collections.Generic;
using Deneblab.StashLock.Cli.Modules.Ecies;
using Xunit;

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
    public void ComputeMac_ShouldBeDeterministic()
    {
        var dataKey = AesGcmHelper.GenerateDataKey();
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["key1"] = "ENC[AesGcm:abc]",
            ["key2"] = "ENC[AesGcm:def]"
        };

        var mac1 = AesGcmHelper.ComputeMac(dataKey, values);
        var mac2 = AesGcmHelper.ComputeMac(dataKey, values);

        Assert.Equal(mac1, mac2);
    }

    [Fact]
    public void VerifyMac_ShouldReturnTrueForValidMac()
    {
        var dataKey = AesGcmHelper.GenerateDataKey();
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["db"] = "ENC[AesGcm:abc]",
            ["api_key"] = "ENC[AesGcm:def]"
        };

        var mac = AesGcmHelper.ComputeMac(dataKey, values);

        Assert.True(AesGcmHelper.VerifyMac(dataKey, values, mac));
    }

    [Fact]
    public void VerifyMac_ShouldReturnFalseForTamperedValues()
    {
        var dataKey = AesGcmHelper.GenerateDataKey();
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["key1"] = "ENC[AesGcm:abc]"
        };

        var mac = AesGcmHelper.ComputeMac(dataKey, values);

        // Tamper with the values
        values["key1"] = "ENC[AesGcm:tampered]";

        Assert.False(AesGcmHelper.VerifyMac(dataKey, values, mac));
    }

    [Fact]
    public void ComputeMac_ShouldDifferWithDifferentKeys()
    {
        var key1 = AesGcmHelper.GenerateDataKey();
        var key2 = AesGcmHelper.GenerateDataKey();
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["key1"] = "value1"
        };

        var mac1 = AesGcmHelper.ComputeMac(key1, values);
        var mac2 = AesGcmHelper.ComputeMac(key2, values);

        Assert.NotEqual(mac1, mac2);
    }
}
