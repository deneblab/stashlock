using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Deneblab.StashLock.Cli.Common;
using Deneblab.StashLock.Cli.Modules.Encode;
using Deneblab.StashLock.Cli.Modules.Init;
using Deneblab.StashLock.Cli.Modules.Publish;
using Deneblab.StashLock.Client.Common.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using StashLockClient = Deneblab.StashLock.Client.StashLock;

namespace Deneblab.StashLock.Tests.Contract;

/// <summary>
/// End-to-end contract between the .NET CLI/client and the real Go server.
/// Runs only when STASHLOCK_TEST_SERVER_URL and STASHLOCK_TEST_MASTER_KEY point at a fresh server
/// (the master key is single-use); otherwise each test returns immediately.
/// </summary>
[Collection(nameof(Cli.EnvironmentVariablesCollection))]
public class ServerContractTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string? _url = Environment.GetEnvironmentVariable("STASHLOCK_TEST_SERVER_URL");
    private readonly string? _masterKey = Environment.GetEnvironmentVariable("STASHLOCK_TEST_MASTER_KEY");
    private readonly string _dir = Directory.CreateTempSubdirectory("stashlock-contract-").FullName;
    private static string? _adminKey;

    public ServerContractTests(ITestOutputHelper output) => _out = output;

    public void Dispose() => Directory.Delete(_dir, true);

    private bool Enabled()
    {
        if (!string.IsNullOrEmpty(_url) && !string.IsNullOrEmpty(_masterKey)) return true;
        _out.WriteLine("Skipped: set STASHLOCK_TEST_SERVER_URL and STASHLOCK_TEST_MASTER_KEY to run contract tests.");
        return false;
    }

    private async Task<string> AdminKey()
    {
        if (_adminKey != null) return _adminKey;
        using var http = new HttpClient();
        var resp = await http.PostAsync(_url + "/admin/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["apiKey"] = _masterKey! }));
        var html = await resp.Content.ReadAsStringAsync();
        var match = Regex.Match(html, @"slk_adm_[A-Za-z0-9]+");
        Assert.True(match.Success, "Master key exchange did not return an admin key (is the server fresh?)");
        return _adminKey = match.Value;
    }

    private async Task<string> CreateApiKey(string box, string tag, bool write)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await AdminKey());
        var body = JsonSerializer.Serialize(new { name = $"contract-{tag}-{Guid.NewGuid():N}", box, tag, canRead = true, canWrite = write });
        var resp = await http.PostAsync(_url + "/keys", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("key").GetString()!;
    }

    private async Task<(string Box, string PrivateKey, string File)> EncryptSample(string value)
    {
        var vault = Path.Combine(_dir, "vault");
        await new InitCommands(NullLogger<InitCommands>.Instance).Root(vault, "dev");
        File.WriteAllText(Path.Combine(vault, "secrets.json"), $"{{\"Db\":\"{value}\",\"Nested\":{{\"Port\":\"5432\"}}}}");
        await new GenerateCommands(NullLogger<GenerateCommands>.Instance).Root(Path.Combine(vault, "secrets.json"));
        var box = JsonDocument.Parse(File.ReadAllText(Path.Combine(vault, "stashlock.config.json"))).RootElement.GetProperty("Name").GetString()!;
        var key = JsonDocument.Parse(File.ReadAllText(Path.Combine(vault, "stashlock.key.dev.json"))).RootElement.GetProperty("PrivateKey").GetString()!;
        return (box, key, Path.Combine(vault, "stashlock.enc.dev.secrets.json"));
    }

    [Fact]
    public async Task PublishWithCli_ReadWithClient_ScopedToBoxTag()
    {
        if (!Enabled()) return;
        var value = "contract-" + Guid.NewGuid().ToString("N")[..8];
        var (box, privateKey, file) = await EncryptSample(value);

        // CI key (Read + Write) publishes what `stashlock encode` produced
        var ciKey = await CreateApiKey(box, "dev", write: true);
        var code = await new PublishCommands(NullLogger<PublishCommands>.Instance).Root(file, url: _url!, apiKey: ciKey);
        Assert.Equal(ExitCodes.Ok, code);

        // App key (Read) decrypts it with the client library
        var appKey = await CreateApiKey(box, "dev", write: false);
        var store = await StashLockClient.CreateClient()
            .WithBox(box, "dev").WithApiUrl(_url!).WithApiKey(appKey).WithPrivateKey(privateKey)
            .OpenAsync();
        Assert.Equal(value, store["Db"]);
        Assert.Equal("5432", store["Nested:Port"]);

        // A read-only key cannot publish
        Assert.Equal(ExitCodes.Error, await new PublishCommands(NullLogger<PublishCommands>.Instance).Root(file, url: _url!, apiKey: appKey));

        // A key for another tag is refused, never served from anywhere else
        var otherKey = await CreateApiKey(box, "prod", write: false);
        var denied = await Assert.ThrowsAsync<VaultAccessDeniedException>(() => StashLockClient.CreateClient()
            .WithBox(box, "dev").WithApiUrl(_url!).WithApiKey(otherKey).WithPrivateKey(privateKey).OpenAsync());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // A wrong key gets 401
        var wrong = await Assert.ThrowsAsync<VaultAccessDeniedException>(() => StashLockClient.CreateClient()
            .WithBox(box, "dev").WithApiUrl(_url!).WithApiKey("slk_wrong").WithPrivateKey(privateKey).OpenAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task AdminKey_CannotReadSecrets_MasterKeyRejectedByApi()
    {
        if (!Enabled()) return;
        using var http = new HttpClient();

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await AdminKey());
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(_url + "/boxes")).StatusCode);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _masterKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(_url + "/keys")).StatusCode);
    }
}
