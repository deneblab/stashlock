using System.Collections.Generic;
using System.Text.Json;
using Deneblab.StashLock.Cli.Models;
using Xunit;

namespace Deneblab.StashLock.Tests.Cli;

public class StashlockConfigTests
{
    [Fact]
    public void Defaults_ShouldHaveExpectedValues()
    {
        var config = new StashlockConfig();

        Assert.Equal("00001", config.Version);
        Assert.Equal(string.Empty, config.Name);
        Assert.Empty(config.Tags);
        Assert.Equal("sops", config.EncryptionMode);
        Assert.Equal(string.Empty, config.ServerUrl);
        Assert.Equal(string.Empty, config.ApiKey);
    }

    [Fact]
    public void JsonRoundTrip_ShouldPreserveAllProperties()
    {
        var original = new StashlockConfig
        {
            Version = "00002",
            Name = "my-vault",
            Tags = new List<string> { "production", "staging" },
            EncryptionMode = "wholefile",
            ServerUrl = "https://vault.example.com",
            ApiKey = "my-secret-key"
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<StashlockConfig>(json)!;

        Assert.Equal(original.Version, deserialized.Version);
        Assert.Equal(original.Name, deserialized.Name);
        Assert.Equal(original.Tags, deserialized.Tags);
        Assert.Equal(original.EncryptionMode, deserialized.EncryptionMode);
        Assert.Equal(original.ServerUrl, deserialized.ServerUrl);
        Assert.Equal(original.ApiKey, deserialized.ApiKey);
    }

    [Fact]
    public void JsonPropertyNames_ShouldBePascalCase()
    {
        var config = new StashlockConfig { Name = "test", Tags = new List<string> { "dev" } };
        var json = JsonSerializer.Serialize(config);

        Assert.Contains("\"Version\"", json);
        Assert.Contains("\"Name\"", json);
        Assert.Contains("\"Tags\"", json);
        Assert.Contains("\"EncryptionMode\"", json);
        Assert.Contains("\"ServerUrl\"", json);
        Assert.Contains("\"ApiKey\"", json);
    }

    [Fact]
    public void Deserialize_ShouldHandleEmptyTags()
    {
        var json = "{\"Version\":\"00001\",\"Name\":\"test\",\"Tags\":[]}";
        var config = JsonSerializer.Deserialize<StashlockConfig>(json)!;

        Assert.Empty(config.Tags);
    }

    [Fact]
    public void Deserialize_ShouldHandleMissingServerUrlAndApiKey()
    {
        var json = "{\"Version\":\"00001\",\"Name\":\"test\",\"Tags\":[],\"EncryptionMode\":\"sops\"}";
        var config = JsonSerializer.Deserialize<StashlockConfig>(json)!;

        Assert.Equal(string.Empty, config.ServerUrl);
        Assert.Equal(string.Empty, config.ApiKey);
    }

    [Fact]
    public void Deserialize_ShouldHandleMultipleTags()
    {
        var json = "{\"Version\":\"00001\",\"Name\":\"test\",\"Tags\":[\"prod\",\"staging\",\"dev\"]}";
        var config = JsonSerializer.Deserialize<StashlockConfig>(json)!;

        Assert.Equal(3, config.Tags.Count);
        Assert.Contains("prod", config.Tags);
        Assert.Contains("staging", config.Tags);
        Assert.Contains("dev", config.Tags);
    }
}
