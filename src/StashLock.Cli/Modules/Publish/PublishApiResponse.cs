using System.Text.Json.Serialization;

namespace Deneblab.StashLock.Cli.Modules.Publish;

internal class PublishApiResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
