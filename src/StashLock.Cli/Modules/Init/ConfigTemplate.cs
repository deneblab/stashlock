using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Deneblab.StashLock.Cli.Modules.Init;

/// <summary>
/// Text of the <c>stashlock.config.json</c> written by <c>init</c>: every setting with a comment,
/// optional ones commented out. Every command reads the file with comments allowed.
/// </summary>
internal static class ConfigTemplate
{
    private static readonly JsonSerializerOptions ValueOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Render(string name, IReadOnlyList<string> tags, string version, string serverUrl)
    {
        IReadOnlyList<string> apiKeyTags = tags.Count > 0 ? tags : ["production"];
        var apiKeyLines = string.Join(",\n", apiKeyTags.Select(t => $"  //   {Json(t)}: \"slk_...\""));

        var serverLine = string.IsNullOrWhiteSpace(serverUrl)
            ? "  // \"ServerUrl\": \"https://vault.example.com\","
            : $"  \"ServerUrl\": {Json(serverUrl)},";

        return $$"""
            // StashLock vault configuration (JSON with comments).
            // `stashlock init` adds this file and stashlock.key.*.json (private keys) to .gitignore:
            // it may hold API keys (ApiKeys below), so do not commit it.
            {
              // Box name on the server. With a tag and the version it forms the vault key <Name>.<tag>.<Version>.
              "Name": {{Json(name)}},

              // Environment tags. `stashlock keygen` creates stashlock.key.<tag>.json for each one;
              // `stashlock encode` encrypts for every tag whose key file is in this directory.
              "Tags": [{{string.Join(", ", tags.Select(Json))}}],

              // Version part of the vault key (five digits). Raise it to publish a new entry
              // (e.g. a breaking change of the secrets) without overwriting the current one.
              "Version": {{Json(version)}},

              // Server for `stashlock publish` and `stashlock cstring`; --url and STASHLOCK_API_URL take precedence.
            {{serverLine}}

              // API key per tag (created by an admin for <Name>.<tag>) for `stashlock publish` and `stashlock cstring`;
              // --api-key and STASHLOCK_API_KEY take precedence. Keep this file out of git when it holds keys.
              // "ApiKeys": {
            {{apiKeyLines}}
              // },

              // Public skeleton of the secrets. When this section is present, `stashlock encode <FILE>.json`
              // also writes stashlock.skeleton.<FILE>.json: the key names of every tag, each value replaced
              // by "text". It holds no values, so it is safe to commit (e.g. as context for people and LLMs).
              // "Skeleton": {
              //   // Keys left out of the skeleton, e.g. "Customers:*" ('*' matches anything, case-insensitive)
              //   "Exclude": []
              // }
            }

            """;
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ValueOptions);
}
