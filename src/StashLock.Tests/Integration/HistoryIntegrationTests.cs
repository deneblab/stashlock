using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Deneblab.StashLock.Server;
using Deneblab.StashLock.Server.Common.Misc;
using Deneblab.StashLock.Server.Common.Simple;
using Deneblab.StashLock.Server.Data;
using Deneblab.StashLock.Server.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Deneblab.StashLock.Tests.Integration;

public class HistoryIntegrationTests : IClassFixture<HistoryIntegrationTests.HistoryFactory>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _client;

    public HistoryIntegrationTests(HistoryFactory factory)
    {
        _client = factory.CreateClient();
    }

    public class HistoryFactory : WebApplicationFactory<Program>
    {
        private static readonly string DbName = "HistoryTests-" + Guid.NewGuid();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var regDescriptors = services.Where(
                    d => d.ServiceType == typeof(Registry)).ToList();
                foreach (var d in regDescriptors) services.Remove(d);

                var env = new SimpleEnvResult(
                    SimpleAppMode.Test,
                    Path.GetTempPath(),
                    Path.GetTempPath(),
                    Path.GetTempPath()
                );
                var version = new SimpleVersionInfo(
                    "TestProduct", "1.0.0-test", 0, "test",
                    DateTime.UtcNow, "test", "abc123", 0,
                    new Dictionary<string, string>()
                );
                services.AddSingleton(new Registry(env, version));

                var dbContextDescriptors = services.Where(
                    d => d.ServiceType == typeof(DbContextOptions<StashLockDbContext>)
                      || d.ServiceType == typeof(StashLockDbContext)
                      || d.ServiceType.FullName?.Contains("EntityFrameworkCore") == true
                ).ToList();
                foreach (var d in dbContextDescriptors) services.Remove(d);

                services.AddDbContext<StashLockDbContext>((sp, options) =>
                {
                    options.UseInMemoryDatabase(DbName);
                });
            });
        }
    }

    private static string EncodeKey(string key) => Helpers.Base64UrlEncode(key);

    [Fact]
    public async Task HistoryEndpoint_ReturnsEmptyForNewKey()
    {
        var encodedKey = EncodeKey("histint.new.v1");
        await _client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("first value", Encoding.UTF8, "text/plain"));

        var response = await _client.GetAsync($"/boxes/{encodedKey}/history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var history = JsonSerializer.Deserialize<HistoryResponse>(body, JsonOpts);
        Assert.NotNull(history);
        Assert.Equal("histint.new.v1", history.Key);
        Assert.Equal(0, history.TotalVersions);
        Assert.Empty(history.Versions);
    }

    [Fact]
    public async Task HistoryEndpoint_ReturnsPreviousVersionsAfterUpdates()
    {
        var encodedKey = EncodeKey("histint.updates.v1");
        await _client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("v1-value", Encoding.UTF8, "text/plain"));
        await _client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("v2-value", Encoding.UTF8, "text/plain"));
        await _client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("v3-value", Encoding.UTF8, "text/plain"));

        var response = await _client.GetAsync($"/boxes/{encodedKey}/history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var history = JsonSerializer.Deserialize<HistoryResponse>(body, JsonOpts);
        Assert.NotNull(history);
        Assert.Equal(2, history.TotalVersions);
        Assert.Equal(2, history.Versions.Count);
    }

    [Fact]
    public async Task HistoryVersionEndpoint_ReturnsOldValueAsPlainText()
    {
        var encodedKey = EncodeKey("histint.version.v1");
        await _client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("original-secret", Encoding.UTF8, "text/plain"));
        await _client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("updated-secret", Encoding.UTF8, "text/plain"));

        var response = await _client.GetAsync($"/boxes/{encodedKey}/history/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("original-secret", content);
    }

    [Fact]
    public async Task HistoryVersionEndpoint_Returns404ForNonExistentVersion()
    {
        var encodedKey = EncodeKey("histint.missing.v1");
        await _client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("some value", Encoding.UTF8, "text/plain"));

        var response = await _client.GetAsync($"/boxes/{encodedKey}/history/99");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Metadata_IncludesVersionCount()
    {
        var encodedKey = EncodeKey("histint.meta.v1");
        await _client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("v1", Encoding.UTF8, "text/plain"));
        await _client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("v2", Encoding.UTF8, "text/plain"));

        var response = await _client.GetAsync($"/boxes/{encodedKey}/meta");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var meta = JsonSerializer.Deserialize<KeyMetadata>(body, JsonOpts);
        Assert.NotNull(meta);
        Assert.Equal(1, meta.VersionCount);
    }
}
