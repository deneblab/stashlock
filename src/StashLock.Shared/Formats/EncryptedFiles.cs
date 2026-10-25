#nullable enable
// Shared source: compiled into StashLock.Cli and StashLock.Client (see StashLock.Shared/README.md)
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Deneblab.StashLock.Shared.Formats;

/// <summary>
///     SOPS-mode encrypted file format. Written by the CLI, read by the CLI and the client.
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
    public string Mode { get; set; } = SopsCodec.Mode;

    [JsonPropertyName("dataKey")]
    public string DataKey { get; set; } = string.Empty;

    [JsonPropertyName("mac")]
    public string Mac { get; set; } = string.Empty;
}
