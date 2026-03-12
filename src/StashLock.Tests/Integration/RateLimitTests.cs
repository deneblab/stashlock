using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Deneblab.StashLock.Server;
using Deneblab.StashLock.Server.Common;
using Deneblab.StashLock.Server.Common.Simple;
using Deneblab.StashLock.Server.Data;
using Deneblab.StashLock.Server.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Deneblab.StashLock.Tests.Integration;

public class RateLimitTests : IClassFixture<RateLimitTests.RateLimitFactory>
{
    private const string MasterKey = "master-key-for-rate-limit-tests";
    private const int GlobalLimit = 5;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly RateLimitFactory _factory;

    public RateLimitTests(RateLimitFactory factory)
    {
        _factory = factory;
    }

    public class RateLimitFactory : WebApplicationFactory<Program>
    {
        private static readonly string DbName = "RateLimitTests-" + Guid.NewGuid();

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
                    opts.DefaultRateLimitPerMinute = GlobalLimit;
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

    private HttpClient CreateMasterClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MasterKey);
        return client;
    }

    private HttpClient CreateClientWithKey(string apiKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private async Task<string> CreateScopedKey(string name, int? rateLimitPerMinute = null)
    {
        using var master = CreateMasterClient();
        var request = new CreateApiKeyRequest
        {
            Name = name,
            RateLimitPerMinute = rateLimitPerMinute
        };

        var json = JsonSerializer.Serialize(request);
        var response = await master.PostAsync("/keys",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<CreateApiKeyResponse>(body, JsonOpts);
        return result!.Key;
    }

    [Fact]
    public async Task ScopedKey_WithCustomLimit_ShouldReturn429AfterExceeding()
    {
        var key = await CreateScopedKey("rate-limited-3", rateLimitPerMinute: 3);
        using var client = CreateClientWithKey(key);

        // First 3 requests should succeed
        for (int i = 0; i < 3; i++)
        {
            var response = await client.GetAsync("/boxes");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // 4th request should be rate limited
        var limitedResponse = await client.GetAsync("/boxes");
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);

        var body = await limitedResponse.Content.ReadAsStringAsync();
        Assert.Contains("RATE_LIMITED", body);
        Assert.True(limitedResponse.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task MasterKey_ShouldNotBeRateLimited()
    {
        using var master = CreateMasterClient();

        // Make many requests — all should succeed
        for (int i = 0; i < 20; i++)
        {
            var response = await master.GetAsync("/boxes");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task ScopedKey_WithNoCustomLimit_ShouldUseGlobalDefault()
    {
        var key = await CreateScopedKey("global-default");
        using var client = CreateClientWithKey(key);

        // GlobalLimit is 5, first 5 should succeed
        for (int i = 0; i < GlobalLimit; i++)
        {
            var response = await client.GetAsync("/boxes");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // 6th should be rate limited
        var limitedResponse = await client.GetAsync("/boxes");
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);
    }

    [Fact]
    public async Task HealthAndRoot_ShouldBypassRateLimiting()
    {
        var key = await CreateScopedKey("bypass-check", rateLimitPerMinute: 2);
        // Note: health/root endpoints don't require auth, so we test without auth
        var client = _factory.CreateClient();

        for (int i = 0; i < 10; i++)
        {
            var rootResponse = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, rootResponse.StatusCode);

            var healthResponse = await client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        }
    }
}
