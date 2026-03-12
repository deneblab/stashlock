using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

public class DashboardIntegrationTests : IClassFixture<DashboardIntegrationTests.DashboardFactory>
{
    private const string MasterKey = "master-key-for-dashboard-tests";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly DashboardFactory _factory;

    public DashboardIntegrationTests(DashboardFactory factory)
    {
        _factory = factory;
    }

    public class DashboardFactory : WebApplicationFactory<Program>
    {
        private static readonly string DbName = "DashboardTests-" + Guid.NewGuid();

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

                services.Configure<StashLockOptions>(opts =>
                {
                    opts.ApiKey = MasterKey;
                });

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

    private HttpClient CreateMasterClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MasterKey);
        return client;
    }

    private HttpClient CreateUnauthClient()
    {
        return _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task Dashboard_WithoutAuth_ShouldRedirectToLogin()
    {
        using var client = CreateUnauthClient();
        var response = await client.GetAsync("/admin");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/login", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Stats_WithMasterKey_ShouldReturn200()
    {
        using var client = CreateMasterClient();
        var response = await client.GetAsync("/stats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var stats = JsonSerializer.Deserialize<StatsResponse>(body, JsonOpts);
        Assert.NotNull(stats);
        Assert.Equal("1.0.0-test", stats.ServerVersion);
    }

    [Fact]
    public async Task Stats_WithoutAuth_ShouldReturn401()
    {
        using var client = CreateUnauthClient();
        var response = await client.GetAsync("/stats");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Stats_WithScopedKey_ShouldReturn403()
    {
        using var master = CreateMasterClient();

        // Create a scoped key
        var createReq = new CreateApiKeyRequest { Name = "stats-scoped-test" };
        var json = JsonSerializer.Serialize(createReq);
        var createResp = await master.PostAsync("/keys",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var createBody = await createResp.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<CreateApiKeyResponse>(createBody, JsonOpts);

        // Use scoped key to access stats
        using var scoped = _factory.CreateClient();
        scoped.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created!.Key);
        var response = await scoped.GetAsync("/stats");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Stats_ReturnsAccurateCounts()
    {
        using var client = CreateMasterClient();

        // Create two boxes
        var key1 = EncodeKey("dash.count.v1");
        var key2 = EncodeKey("dash.count.v2");
        await client.PostAsync($"/boxes/{key1}",
            new StringContent("value-one", Encoding.UTF8, "text/plain"));
        await client.PostAsync($"/boxes/{key2}",
            new StringContent("value-two", Encoding.UTF8, "text/plain"));

        // Delete one
        await client.DeleteAsync($"/boxes/{key2}");

        // Get stats
        var response = await client.GetAsync("/stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var stats = JsonSerializer.Deserialize<StatsResponse>(body, JsonOpts);

        Assert.NotNull(stats);
        Assert.True(stats.TotalKeys >= 2, "TotalKeys should be at least 2");
        Assert.True(stats.ActiveKeys >= 1, "ActiveKeys should be at least 1");
        Assert.True(stats.DeletedKeys >= 1, "DeletedKeys should be at least 1");
        Assert.True(stats.TotalSizeBytes > 0, "TotalSizeBytes should be greater than 0");
    }
}
