using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Deneblab.StashLock.Cli.Common;
using Deneblab.StashLock.Cli.Modules.Decode;
using Deneblab.StashLock.Cli.Modules.Encode;
using Deneblab.StashLock.Cli.Modules.Init;
using Deneblab.StashLock.Cli.Modules.Keygen;
using Deneblab.StashLock.Cli.Modules.Publish;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Deneblab.StashLock.Tests.Cli;

// Commands print to the shared Console, so these run with the environment-variable tests, not in parallel
[Collection(nameof(EnvironmentVariablesCollection))]
public class CliExitCodeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("stashlock-exit-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string Vault(string tags = "dev")
    {
        var vault = Path.Combine(_dir, "vault");
        Assert.Equal(ExitCodes.Ok, new InitCommands(NullLogger<InitCommands>.Instance).Root(vault, tags).Result);
        File.WriteAllText(Path.Combine(vault, "secrets.json"), "{\"Db\":\"secret\"}");
        Assert.Equal(ExitCodes.Ok, new GenerateCommands(NullLogger<GenerateCommands>.Instance).Root(Path.Combine(vault, "secrets.json")).Result);
        return vault;
    }

    [Fact]
    public async Task Decode_MissingFile_ReturnsErrorOnStderr()
    {
        var err = new StringWriter();
        var oldErr = Console.Error;
        Console.SetError(err);
        try
        {
            var code = await new DecodeCommands(NullLogger<DecodeCommands>.Instance).Root(Path.Combine(_dir, "nope.json"));
            Assert.Equal(ExitCodes.Error, code);
            Assert.Contains("Encrypted file not found", err.ToString());
        }
        finally
        {
            Console.SetError(oldErr);
        }
    }

    [Fact]
    public async Task Init_Twice_SecondReturnsError()
    {
        var vault = Vault();
        Assert.Equal(ExitCodes.Error, await new InitCommands(NullLogger<InitCommands>.Instance).Root(vault, "dev"));
    }

    [Fact]
    public async Task Keygen_WithoutConfig_ReturnsError()
    {
        Assert.Equal(ExitCodes.Error, await new KeyCommands(NullLogger<KeyCommands>.Instance).Root(_dir));
    }

    [Fact]
    public async Task Decode_RoundTrip_ReturnsOk()
    {
        var vault = Vault();
        var code = await new DecodeCommands(NullLogger<DecodeCommands>.Instance)
            .Root(Path.Combine(vault, "stashlock.enc.dev.secrets.json"), output: Path.Combine(_dir, "out.json"));
        Assert.Equal(ExitCodes.Ok, code);
    }

    [Fact]
    public async Task Publish_WithoutUrlOrKey_ReturnsError()
    {
        var vault = Vault();
        var publish = new PublishCommands(NullLogger<PublishCommands>.Instance);
        Environment.SetEnvironmentVariable("STASHLOCK_API_URL", null);
        Environment.SetEnvironmentVariable("STASHLOCK_API_KEY", null);
        File.WriteAllText(Path.Combine(vault, "stashlock.config.json"), "{\"Name\":\"vault\",\"Tags\":[\"dev\"]}");
        Assert.Equal(ExitCodes.Error, await publish.Root(vault, apiKey: "slk_x"));
        Assert.Equal(ExitCodes.Error, await publish.Root(vault, url: "http://127.0.0.1:9"));
    }

    [Fact]
    public async Task Publish_SomeFilesRejected_ReturnsPartial()
    {
        var vault = Vault("dev,prod");
        using var server = new FakeServer(path => DecodeKey(path).Contains(".dev.") ? 200 : 403);
        var code = await new PublishCommands(NullLogger<PublishCommands>.Instance).Root(vault, url: server.Url, apiKey: "slk_test");
        Assert.Equal(ExitCodes.Partial, code);
    }

    [Fact]
    public async Task Publish_AllRejected_ReturnsError()
    {
        var vault = Vault();
        using var server = new FakeServer(_ => 401);
        var code = await new PublishCommands(NullLogger<PublishCommands>.Instance).Root(vault, url: server.Url, apiKey: "slk_test");
        Assert.Equal(ExitCodes.Error, code);
    }

    [Fact]
    public async Task Publish_ApiKeysFromConfig_UsesKeyOfEachFilesTag()
    {
        var vault = Vault("dev,prod");
        Environment.SetEnvironmentVariable("STASHLOCK_API_KEY", null);
        WriteConfigWithApiKeys(vault, "\"dev\": \"slk_dev\", \"PROD\": \"slk_prod\"");
        using var server = new FakeServer(_ => 200);

        var code = await new PublishCommands(NullLogger<PublishCommands>.Instance).Root(vault, url: server.Url);

        Assert.Equal(ExitCodes.Ok, code);
        var auth = server.Requests.ToDictionary(r => DecodeKey(r.Path), r => r.Authorization);
        Assert.Equal("Bearer slk_dev", auth["vault.dev.00001"]);
        Assert.Equal("Bearer slk_prod", auth["vault.prod.00001"]);
    }

    [Fact]
    public async Task Publish_FlagOverridesApiKeysFromConfig()
    {
        var vault = Vault("dev");
        WriteConfigWithApiKeys(vault, "\"dev\": \"slk_dev\"");
        using var server = new FakeServer(_ => 200);

        Assert.Equal(ExitCodes.Ok, await new PublishCommands(NullLogger<PublishCommands>.Instance).Root(vault, url: server.Url, apiKey: "slk_flag"));
        Assert.Equal("Bearer slk_flag", Assert.Single(server.Requests).Authorization);
    }

    [Fact]
    public async Task Publish_TagMissingFromApiKeys_FailsOnlyThatFile()
    {
        var vault = Vault("dev,prod");
        Environment.SetEnvironmentVariable("STASHLOCK_API_KEY", null);
        WriteConfigWithApiKeys(vault, "\"dev\": \"slk_dev\"");
        using var server = new FakeServer(_ => 200);

        var code = await new PublishCommands(NullLogger<PublishCommands>.Instance).Root(vault, url: server.Url);

        Assert.Equal(ExitCodes.Partial, code);
        Assert.Equal("vault.dev.00001", DecodeKey(Assert.Single(server.Requests).Path));
    }

    [Fact]
    public async Task Publish_KeyFromConfigNotGitIgnored_Warns()
    {
        var vault = Vault("dev");
        Environment.SetEnvironmentVariable("STASHLOCK_API_KEY", null);
        WriteConfigWithApiKeys(vault, "\"dev\": \"slk_dev\"");
        File.Delete(Path.Combine(vault, ".gitignore"));
        using var server = new FakeServer(_ => 200);

        var err = new StringWriter();
        var oldErr = Console.Error;
        Console.SetError(err);
        try
        {
            Assert.Equal(ExitCodes.Ok, await new PublishCommands(NullLogger<PublishCommands>.Instance).Root(vault, url: server.Url));
        }
        finally
        {
            Console.SetError(oldErr);
        }

        Assert.Contains("is not in .gitignore", err.ToString());
    }

    [Fact]
    public void Init_AddsConfigAndKeyFilesToGitIgnore()
    {
        var gitignore = File.ReadAllLines(Path.Combine(Vault("dev"), ".gitignore"));
        Assert.Contains("stashlock.config.json", gitignore);
        Assert.Contains("stashlock.key.*.json", gitignore);
    }

    private static void WriteConfigWithApiKeys(string vault, string apiKeys) =>
        File.WriteAllText(Path.Combine(vault, "stashlock.config.json"),
            "{ // comment\n \"Name\": \"vault\", \"Tags\": [\"dev\", \"prod\"], \"ApiKeys\": { " + apiKeys + " }, }");

    // "/boxes/{base64url(box.tag.version)}" → "box.tag.version"
    private static string DecodeKey(string path)
    {
        var b64 = path[(path.LastIndexOf('/') + 1)..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

}
