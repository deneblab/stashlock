using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Deneblab.StashLock.Client.Common;

/// <summary>
///     SOPS-mode encrypted file format. Matches CLI output.
/// </summary>
internal class SopsEncryptedFile
{
    [JsonPropertyName("values")]
    public Dictionary<string, string> Values { get; set; } = new();

    [JsonPropertyName("_stashlock")]
    public SopsMetadata Metadata { get; set; } = new();
}

/// <summary>
///     Metadata block in SOPS encrypted files.
/// </summary>
internal class SopsMetadata
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

/// <summary>
///     Whole-file mode encrypted file format. Matches CLI output.
/// </summary>
internal class StashlockEncryptedFile
{
    [JsonPropertyName("VaultKey")]
    public string VaultKey { get; set; } = string.Empty;

    [JsonPropertyName("EncryptedData")]
    public string EncryptedData { get; set; } = string.Empty;
}
