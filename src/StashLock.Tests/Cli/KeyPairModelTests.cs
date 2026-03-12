using System;
using System.Text.Json;
using Deneblab.StashLock.Cli.Modules.Ecies;
using Xunit;

namespace Deneblab.StashLock.Tests.Cli;

public class KeyPairModelTests
{
    [Fact]
    public void GetPublicKeyBytes_ShouldDecodeBase64()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var model = new KeyPairModel { PublicKey = Convert.ToBase64String(bytes) };

        Assert.Equal(bytes, model.GetPublicKeyBytes());
    }

    [Fact]
    public void GetPrivateKeyBytes_ShouldDecodeBase64()
    {
        var bytes = new byte[] { 10, 20, 30, 40, 50 };
        var model = new KeyPairModel { PrivateKey = Convert.ToBase64String(bytes) };

        Assert.Equal(bytes, model.GetPrivateKeyBytes());
    }

    [Fact]
    public void JsonRoundTrip_ShouldPreserveAllProperties()
    {
        var original = new KeyPairModel
        {
            Tag = "production",
            PublicKey = Convert.ToBase64String(new byte[32]),
            PrivateKey = Convert.ToBase64String(new byte[32]),
            CreatedAt = "2026-01-15T10:30:00.0000000Z"
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<KeyPairModel>(json)!;

        Assert.Equal(original.Tag, deserialized.Tag);
        Assert.Equal(original.PublicKey, deserialized.PublicKey);
        Assert.Equal(original.PrivateKey, deserialized.PrivateKey);
        Assert.Equal(original.CreatedAt, deserialized.CreatedAt);
    }

    [Fact]
    public void JsonPropertyNames_ShouldBePascalCase()
    {
        var model = new KeyPairModel
        {
            Tag = "test",
            PublicKey = "cHVi",
            PrivateKey = "cHJpdg==",
            CreatedAt = "2026-01-01"
        };

        var json = JsonSerializer.Serialize(model);

        Assert.Contains("\"Tag\"", json);
        Assert.Contains("\"PublicKey\"", json);
        Assert.Contains("\"PrivateKey\"", json);
        Assert.Contains("\"CreatedAt\"", json);
    }
}
