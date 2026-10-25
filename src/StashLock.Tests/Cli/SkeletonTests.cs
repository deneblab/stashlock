using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Deneblab.StashLock.Cli.Common;
using Deneblab.StashLock.Cli.Models;
using Deneblab.StashLock.Cli.Modules.Encode;
using Deneblab.StashLock.Cli.Modules.Init;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Deneblab.StashLock.Tests.Cli;

[Collection(nameof(EnvironmentVariablesCollection))]
public class SkeletonTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("stashlock-skeleton-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string Vault(string tags, bool skeleton, string exclude = "")
    {
        var vault = Path.Combine(_dir, "exampleapp");
        Assert.Equal(ExitCodes.Ok, new InitCommands(NullLogger<InitCommands>.Instance).Root(vault, tags).Result);
        if (skeleton)
            File.WriteAllText(VaultConfig.PathIn(vault), EnableSkeleton(File.ReadAllText(VaultConfig.PathIn(vault)), exclude));
        return vault;
    }

    /// <summary>Uncomments the Skeleton section the way a user would.</summary>
    private static string EnableSkeleton(string config, string exclude)
    {
        var lines = config.Split('\n').ToList();
        var start = lines.FindIndex(l => l.Contains("// \"Skeleton\": {"));
        var end = lines.FindIndex(start, l => l.Trim() == "// }");
        for (var i = start; i <= end; i++)
        {
            var at = lines[i].IndexOf("// ", StringComparison.Ordinal);
            lines[i] = lines[i].Remove(at, 3);
        }
        return string.Join('\n', lines).Replace("\"Exclude\": []", $"\"Exclude\": [{exclude}]");
    }

    private static int Encode(string vault, string json, string file = "secrets.json")
    {
        File.WriteAllText(Path.Combine(vault, file), json);
        return new GenerateCommands(NullLogger<GenerateCommands>.Instance).Root(Path.Combine(vault, file)).Result;
    }

    private static string Skeleton(string vault, string file = "secrets") =>
        File.ReadAllText(Path.Combine(vault, $"stashlock.skeleton.{file}.json"));

    private static string Compact(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);

    [Fact]
    public async Task InitConfig_IsCommentedAndReadable()
    {
        var vault = Path.Combine(_dir, "billing");
        Assert.Equal(ExitCodes.Ok, await new InitCommands(NullLogger<InitCommands>.Instance).Root(vault, "develop,production", url: "https://vault.example.com"));

        var text = File.ReadAllText(VaultConfig.PathIn(vault));
        Assert.Contains("// Box name on the server", text);
        Assert.Contains("// \"Skeleton\": {", text);

        var config = VaultConfig.Load(vault)!;
        Assert.Equal("billing", config.Name);
        Assert.Equal(new[] { "develop", "production" }, config.Tags);
        Assert.Equal("00001", config.Version);
        Assert.Equal("https://vault.example.com", config.ServerUrl);
        Assert.Null(config.Skeleton);
    }

    [Fact]
    public void InitConfig_EscapesValues_AndCommentsOutMissingUrl()
    {
        var text = ConfigTemplate.Render("a\"b", new List<string> { "dev" }, "00001", "");
        Assert.Contains("// \"ServerUrl\":", text);
        var config = JsonSerializer.Deserialize<StashlockConfig>(text, VaultConfig.ReadOptions)!;
        Assert.Equal("a\"b", config.Name);
        Assert.Equal(string.Empty, config.ServerUrl);
    }

    [Fact]
    public void UncommentedSkeletonSection_IsRead()
    {
        var vault = Vault("dev", skeleton: true, exclude: "\"Customers:*\"");
        Assert.Equal(new[] { "Customers:*" }, VaultConfig.Load(vault)!.Skeleton!.Exclude);
    }

    [Fact]
    public void Disabled_WritesNoSkeleton()
    {
        var vault = Vault("dev", skeleton: false);
        Assert.Equal(ExitCodes.Ok, Encode(vault, "{\"Db\":\"x\"}"));
        Assert.False(File.Exists(Path.Combine(vault, "stashlock.skeleton.secrets.json")));
    }

    [Fact]
    public void Enabled_WritesNestedSortedTextOnly()
    {
        var vault = Vault("dev", skeleton: true);
        Assert.Equal(ExitCodes.Ok, Encode(vault,
            "{\"Zeta\":\"very-long-secret-value-0123456789\",\"Database\":{\"Port\":5432,\"Password\":\"s3cret\"},\"Hosts\":[\"h1\",\"h2\"],\"On\":true}"));

        var text = Skeleton(vault);
        Assert.Equal(
            "{\"Database\":{\"Password\":\"text\",\"Port\":\"text\"},\"Hosts\":[\"text\",\"text\"],\"On\":\"text\",\"Zeta\":\"text\"}",
            Compact(text));
        foreach (var secret in new[] { "s3cret", "5432", "h1", "true", "very-long" })
            Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public void Exclude_LeavesKeysOut_CaseInsensitive()
    {
        var vault = Vault("dev", skeleton: true, exclude: "\"customers:*\", \"Db:Password\"");
        Encode(vault, "{\"Customers\":{\"Acme\":{\"ApiKey\":\"k\"}},\"Db\":{\"Password\":\"p\",\"Host\":\"h\"}}");
        Assert.Equal("{\"Db\":{\"Host\":\"text\"}}", Compact(Skeleton(vault)));
    }

    [Fact]
    public void SeveralInputFiles_GetTheirOwnSkeleton()
    {
        var vault = Vault("dev", skeleton: true);
        Encode(vault, "{\"A\":\"1\"}", "secrets.json");
        Encode(vault, "{\"B\":\"2\"}", "db.json");
        Assert.Equal("{\"A\":\"text\"}", Compact(Skeleton(vault, "secrets")));
        Assert.Equal("{\"B\":\"text\"}", Compact(Skeleton(vault, "db")));
    }

    [Fact]
    public void TagsWithDifferentKeys_AreMerged_WithWarning()
    {
        var vault = Vault("develop,production", skeleton: true);
        Encode(vault, "{\"Db\":\"x\",\"ApiToken\":\"t\"}");

        // production encoded later from another machine with an older file: only its key file is present
        var prodKey = Path.Combine(vault, "stashlock.key.production.json");
        var devKey = Path.Combine(vault, "stashlock.key.develop.json");
        File.Move(devKey, devKey + ".away");
        Encode(vault, "{\"Db\":\"x\",\"Old\":\"o\"}");
        File.Move(devKey + ".away", devKey);
        Assert.True(File.Exists(prodKey));

        var result = SkeletonWriter.Write(vault, "secrets", new SkeletonSettings());
        Assert.Equal("{\"ApiToken\":\"text\",\"Db\":\"text\",\"Old\":\"text\"}", result.Skeleton.ToJsonString());
        Assert.Contains("ApiToken: in develop, missing in production", result.Warnings);
        Assert.Contains("Old: in production, missing in develop", result.Warnings);
    }

    [Fact]
    public void KeyRemovedFromAllTags_DisappearsFromSkeleton()
    {
        var vault = Vault("develop,production", skeleton: true);
        Encode(vault, "{\"Db\":\"x\",\"Gone\":\"g\"}");
        Encode(vault, "{\"Db\":\"x\"}");
        Assert.Equal("{\"Db\":\"text\"}", Compact(Skeleton(vault)));
    }

    [Fact]
    public void ForeignEncFile_IsSkippedWithWarning()
    {
        var vault = Vault("dev", skeleton: true);
        Encode(vault, "{\"Db\":\"x\"}");
        File.WriteAllText(Path.Combine(vault, "stashlock.enc.other.secrets.json"), "{\"values\":{\"X\":\"y\"}}");
        var result = SkeletonWriter.Write(vault, "secrets", new SkeletonSettings());
        Assert.Equal("{\"Db\":\"text\"}", result.Skeleton.ToJsonString());
        Assert.Contains(result.Warnings, w => w.StartsWith("stashlock.enc.other.secrets.json skipped"));
    }

    [Fact]
    public void ValueAndSectionConflict_KeepsSection()
    {
        var warnings = new List<string>();
        var obj = SkeletonWriter.Build(new[] { "A", "A:B" }, warnings);
        Assert.Equal("{\"A\":{\"B\":\"text\"}}", obj.ToJsonString());
        Assert.Single(warnings);
    }

    [Fact]
    public void SparseNumericKeys_StayAnObject()
    {
        var obj = SkeletonWriter.Build(new[] { "L:0", "L:2", "M:0", "M:1", "M:10", "M:2", "M:3", "M:4", "M:5", "M:6", "M:7", "M:8", "M:9" }, new List<string>());
        Assert.Equal("{\"0\":\"text\",\"2\":\"text\"}", obj["L"]!.ToJsonString());
        Assert.Equal(11, obj["M"]!.AsArray().Count);
    }
}
