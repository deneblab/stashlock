using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Deneblab.StashLock.Cli.Modules.Encode;

public class SopsEncryptedFile
{
    [JsonPropertyName("values")]
    public Dictionary<string, string> Values { get; set; } = new();

    [JsonPropertyName("_stashlock")]
    public SopsMetadata Metadata { get; set; } = new();
}

public class SopsMetadata
{
    [JsonPropertyName("vaultKey")]
    public string VaultKey { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "sops";

    [JsonPropertyName("dataKey")]
    public string DataKey { get; set; } = string.Empty;

    [JsonPropertyName("mac")]
    public string Mac { get; set; } = string.Empty;
}
