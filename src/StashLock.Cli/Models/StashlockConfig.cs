using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Deneblab.StashLock.Cli.Models;

public class StashlockConfig
{
    [JsonPropertyName("Version")]
    public string Version { get; set; } = "00001";

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Tags")]
    public List<string> Tags { get; set; } = new List<string>();

    [JsonPropertyName("EncryptionMode")]
    public string EncryptionMode { get; set; } = "sops";

    [JsonPropertyName("ServerUrl")]
    public string ServerUrl { get; set; } = string.Empty;

    [JsonPropertyName("ApiKey")]
    public string ApiKey { get; set; } = string.Empty;
}
