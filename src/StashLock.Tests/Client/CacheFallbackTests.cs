using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Deneblab.StashLock.Cli.Modules.Encode;
using Deneblab.StashLock.Cli.Modules.Init;
using Deneblab.StashLock.Client;
using Deneblab.StashLock.Client.Common.Exceptions;
using Deneblab.StashLock.Client.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using StashLockClient = Deneblab.StashLock.Client.StashLock;

namespace Deneblab.StashLock.Tests.Client;

/// <summary>Cache may stand in for the server only when the server cannot answer.</summary>
[Collection(nameof(Cli.EnvironmentVariablesCollection))]
public class CacheFallbackTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("stashlock-cache-").FullName;
    private int _status = 200;
    private string _encrypted = "";

    public void Dispose() => Directory.Delete(_dir, true);

    private async Task<(string Box, string PrivateKey)> EncryptSample()
    {
        var vault = Path.Combine(_dir, "vault");
        await new InitCommands(NullLogger<InitCommands>.Instance).Root(vault, "dev");
        File.WriteAllText(Path.Combine(vault, "secrets.json"), "{\"Db\":\"secret-1\"}");
        await new GenerateCommands(NullLogger<GenerateCommands>.Instance).Root(Path.Combine(vault, "secrets.json"));
        _encrypted = File.ReadAllText(Path.Combine(vault, "stashlock.enc.dev.secrets.json"));
        var box = JsonDocument.Parse(File.ReadAllText(Path.Combine(vault, "stashlock.config.json"))).RootElement.GetProperty("Name").GetString()!;
        var key = JsonDocument.Parse(File.ReadAllText(Path.Combine(vault, "stashlock.key.dev.json"))).RootElement.GetProperty("PrivateKey").GetString()!;
        return (box, key);
    }

    private Task<ISecretsStore> Open(string url, string box, string privateKey) =>
        StashLockClient.CreateClient()
            .WithBox(box, "dev")
            .WithApiUrl(url)
            .WithApiKey("slk_test")
            .WithPrivateKey(privateKey)
            .WithCache(cacheDir: Path.Combine(_dir, "cache"), machineId: "test-machine", serverTimeout: TimeSpan.FromSeconds(5))
            .OpenAsync();

    [Fact]
    public async Task ServerFirst_UsesCacheOnlyForOutages()
    {
        var (box, privateKey) = await EncryptSample();
        using var server = new FakeServer(_ => (_status, _status == 200 ? _encrypted : "{\"status\":" + _status + "}"));

        // 1. Server answers: value is returned and cached
        var store = await Open(server.Url, box, privateKey);
        Assert.Equal("secret-1", store["Db"]);

        // 2. 500: outage, cache answers
        _status = 500;
        Assert.Equal("secret-1", (await Open(server.Url, box, privateKey))["Db"]);

        // 3. 403 and 401: the key is not allowed; never answered from cache
        _status = 403;
        var denied = await Assert.ThrowsAsync<VaultAccessDeniedException>(() => Open(server.Url, box, privateKey));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);
        _status = 401;
        await Assert.ThrowsAsync<VaultAccessDeniedException>(() => Open(server.Url, box, privateKey));

        // 4. 404: the entry is gone; never answered from cache
        _status = 404;
        await Assert.ThrowsAsync<VaultNotFoundException>(() => Open(server.Url, box, privateKey));
    }

    [Fact]
    public async Task ServerFirst_UnreachableServer_UsesCache()
    {
        var (box, privateKey) = await EncryptSample();
        string url;
        using (var server = new FakeServer(_ => (200, _encrypted)))
        {
            url = server.Url;
            await Open(url, box, privateKey);
        }
        // Server stopped: connection refused counts as an outage
        Assert.Equal("secret-1", (await Open(url, box, privateKey))["Db"]);
    }

    [Theory]
    [InlineData(typeof(VaultNotFoundException), false)]
    [InlineData(typeof(TimeoutException), true)]
    [InlineData(typeof(TaskCanceledException), true)]
    public void IsServerOutage_Classifies(Type type, bool expected)
    {
        var ex = type == typeof(VaultNotFoundException) ? new VaultNotFoundException("k") : (Exception)Activator.CreateInstance(type)!;
        Assert.Equal(expected, SecretsStore.IsServerOutage(ex));
    }

    [Fact]
    public void IsServerOutage_HttpStatus()
    {
        Assert.True(SecretsStore.IsServerOutage(new StashLockException("x", new System.Net.Http.HttpRequestException("down"))));
        Assert.True(SecretsStore.IsServerOutage(new StashLockException("x", new System.Net.Http.HttpRequestException("err", null, System.Net.HttpStatusCode.BadGateway))));
        Assert.False(SecretsStore.IsServerOutage(new StashLockException("x", new System.Net.Http.HttpRequestException("err", null, System.Net.HttpStatusCode.TooManyRequests))));
        Assert.False(SecretsStore.IsServerOutage(new VaultAccessDeniedException("k", System.Net.HttpStatusCode.Forbidden, "no")));
    }

    [Fact]
    public async Task AddStashLock_UsesConnectionStringFromEnvironment()
    {
        var old = Environment.GetEnvironmentVariable("STASHLOCK_CONNECTION_STRING");
        try
        {
            var (box, privateKey) = await EncryptSample();
            using var server = new FakeServer(_ => (200, _encrypted));
            Environment.SetEnvironmentVariable("STASHLOCK_CONNECTION_STRING",
                $"Url={server.Url};ApiKey=slk_test;PrivateKey={privateKey};Box={box};Tag=dev");

            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddStashLock(_ => { })
                .Build();
            Assert.Equal("secret-1", config["Db"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STASHLOCK_CONNECTION_STRING", old);
        }
    }
}
