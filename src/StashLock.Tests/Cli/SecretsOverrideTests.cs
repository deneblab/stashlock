using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Deneblab.StashLock.Cli.Common;
using Deneblab.StashLock.Cli.Modules.Ecies;
using Deneblab.StashLock.Cli.Modules.Encode;
using Deneblab.StashLock.Cli.Modules.Init;
using Deneblab.StashLock.Shared.Formats;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Deneblab.StashLock.Tests.Cli;

[Collection(nameof(EnvironmentVariablesCollection))]
public class SecretsOverrideTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("stashlock-override-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private async Task<string> Vault(string tags = "develop,production")
    {
        var vault = Path.Combine(_dir, "exampleapp");
        Assert.Equal(ExitCodes.Ok, await new InitCommands(NullLogger<InitCommands>.Instance).Root(vault, tags));
        return vault;
    }

    private static Task<int> Encode(string vault, string file = "secrets.json") =>
        new GenerateCommands(NullLogger<GenerateCommands>.Instance).Root(Path.Combine(vault, file));

    private static Dictionary<string, string> Decrypt(string vault, string tag)
    {
        var key = JsonSerializer.Deserialize<KeyPairModel>(File.ReadAllText(Path.Combine(vault, $"stashlock.key.{tag}.json")))!;
        var file = JsonSerializer.Deserialize<SopsEncryptedFile>(File.ReadAllText(Path.Combine(vault, $"stashlock.enc.{tag}.secrets.json")))!;
        return SopsCodec.Decrypt(file, key.GetPrivateKeyBytes());
    }

    private static Dictionary<string, string> Flat(params (string Key, string Value)[] items)
    {
        var result = new Dictionary<string, string>();
        foreach (var (k, v) in items)
            result[k] = v;
        return result;
    }

    [Fact]
    public void Merge_OverridesValues_AndAddsKeys()
    {
        var merged = SecretsOverride.Merge(
            Flat(("Database:Password", "dev"), ("ApiToken", "abc")),
            Flat(("Database:Password", "prod"), ("Monitoring:Key", "m")));

        Assert.Equal(Flat(("Database:Password", "prod"), ("ApiToken", "abc"), ("Monitoring:Key", "m")), merged);
    }

    [Fact]
    public void Merge_ValueReplacesSection_AndSectionReplacesValue()
    {
        var merged = SecretsOverride.Merge(
            Flat(("Database:Host", "h"), ("Database:Port", "1"), ("Cache", "off")),
            Flat(("Database", "Server=prod"), ("Cache:Host", "redis")));

        Assert.Equal(Flat(("Database", "Server=prod"), ("Cache:Host", "redis")), merged);
    }

    [Fact]
    public void Merge_ArraysOverridePerIndex()
    {
        var merged = SecretsOverride.Merge(
            Flat(("Hosts:0", "a"), ("Hosts:1", "b")),
            Flat(("Hosts:0", "x")));

        Assert.Equal(Flat(("Hosts:0", "x"), ("Hosts:1", "b")), merged);
    }

    [Fact]
    public async Task Encode_AppliesOverridePerTag()
    {
        var vault = await Vault();
        File.WriteAllText(Path.Combine(vault, "secrets.json"), """{ "Database": { "Password": "dev" }, "ApiToken": "abc" }""");
        File.WriteAllText(Path.Combine(vault, "secrets.production.json"), """{ "Database": { "Password": "prod" } }""");

        Assert.Equal(ExitCodes.Ok, await Encode(vault));

        Assert.Equal(Flat(("Database:Password", "dev"), ("ApiToken", "abc")), Decrypt(vault, "develop"));
        Assert.Equal(Flat(("Database:Password", "prod"), ("ApiToken", "abc")), Decrypt(vault, "production"));
    }

    [Fact]
    public async Task Encode_InvalidOverride_WritesNothing()
    {
        var vault = await Vault();
        File.WriteAllText(Path.Combine(vault, "secrets.json"), """{ "ApiToken": "abc" }""");
        File.WriteAllText(Path.Combine(vault, "secrets.production.json"), "{ broken");

        Assert.Equal(ExitCodes.Error, await Encode(vault));

        Assert.Empty(Directory.GetFiles(vault, "stashlock.enc.*"));
    }

    [Fact]
    public async Task Encode_OverrideFileAsInput_IsRefused()
    {
        var vault = await Vault();
        File.WriteAllText(Path.Combine(vault, "secrets.json"), """{ "ApiToken": "abc" }""");
        File.WriteAllText(Path.Combine(vault, "secrets.production.json"), """{ "ApiToken": "prod" }""");

        Assert.Equal(ExitCodes.Error, await Encode(vault, "secrets.production.json"));

        Assert.Empty(Directory.GetFiles(vault, "stashlock.enc.*"));
    }

    [Fact]
    public async Task Encode_OverrideAddsKey_SkeletonWarnsButSucceeds()
    {
        var vault = await Vault();
        var config = SkeletonTests.EnableSkeleton(File.ReadAllText(VaultConfig.PathIn(vault)), "");
        File.WriteAllText(VaultConfig.PathIn(vault), config);
        File.WriteAllText(Path.Combine(vault, "secrets.json"), """{ "ApiToken": "abc" }""");
        File.WriteAllText(Path.Combine(vault, "secrets.production.json"), """{ "Monitoring": { "Key": "m" } }""");

        Assert.Equal(ExitCodes.Ok, await Encode(vault));

        var skeleton = JsonDocument.Parse(File.ReadAllText(Path.Combine(vault, "stashlock.skeleton.secrets.json"))).RootElement;
        Assert.Equal("text", skeleton.GetProperty("Monitoring").GetProperty("Key").GetString());
        Assert.False(Decrypt(vault, "develop").ContainsKey("Monitoring:Key"));
    }
}
