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

    [JsonPropertyName("ServerUrl")]
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>Public skeleton written by <c>encode</c>; null (section absent) means off.</summary>
    [JsonPropertyName("Skeleton")]
    public SkeletonSettings? Skeleton { get; set; }
}

public class SkeletonSettings
{
    /// <summary>Key patterns left out of the skeleton (<c>A:B:C</c>, <c>*</c> matches anything, case-insensitive).</summary>
    [JsonPropertyName("Exclude")]
    public List<string> Exclude { get; set; } = new List<string>();
}
