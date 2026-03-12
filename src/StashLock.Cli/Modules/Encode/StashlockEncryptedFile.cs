using System.Text.Json.Serialization;

namespace Deneblab.StashLock.Cli.Modules.Encode;

public class StashlockEncryptedFile
{
    [JsonPropertyName("VaultKey")]
    public string VaultKey { get; set; } = string.Empty;

    [JsonPropertyName("EncryptedData")]
    public string EncryptedData { get; set; } = string.Empty;
}
