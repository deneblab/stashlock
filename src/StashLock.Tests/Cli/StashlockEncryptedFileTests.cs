using System.Text.Json;
using Deneblab.StashLock.Cli.Modules.Encode;
using Xunit;

namespace Deneblab.StashLock.Tests.Cli;

public class StashlockEncryptedFileTests
{
    [Fact]
    public void Defaults_ShouldBeEmpty()
    {
        var file = new StashlockEncryptedFile();

        Assert.Equal(string.Empty, file.VaultKey);
        Assert.Equal(string.Empty, file.EncryptedData);
    }

    [Fact]
    public void JsonRoundTrip_ShouldPreserveProperties()
    {
        var original = new StashlockEncryptedFile
        {
            VaultKey = "myapp.production.00001",
            EncryptedData = "SGVsbG8gV29ybGQ="
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<StashlockEncryptedFile>(json)!;

        Assert.Equal(original.VaultKey, deserialized.VaultKey);
        Assert.Equal(original.EncryptedData, deserialized.EncryptedData);
    }

    [Fact]
    public void JsonPropertyNames_ShouldBePascalCase()
    {
        var file = new StashlockEncryptedFile
        {
            VaultKey = "key",
            EncryptedData = "data"
        };

        var json = JsonSerializer.Serialize(file);

        Assert.Contains("\"VaultKey\"", json);
        Assert.Contains("\"EncryptedData\"", json);
    }
}
