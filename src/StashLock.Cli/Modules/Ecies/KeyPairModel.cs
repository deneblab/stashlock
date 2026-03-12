using System;
using System.Text.Json.Serialization;

namespace Deneblab.StashLock.Cli.Modules.Ecies;

public class KeyPairModel
{
    [JsonPropertyName("Tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("PublicKey")]
    public string PublicKey { get; set; } = string.Empty;

    [JsonPropertyName("PrivateKey")]
    public string PrivateKey { get; set; } = string.Empty;

    [JsonPropertyName("CreatedAt")]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");

    public byte[] GetPublicKeyBytes() => Convert.FromBase64String(PublicKey);
    public byte[] GetPrivateKeyBytes() => Convert.FromBase64String(PrivateKey);
}
