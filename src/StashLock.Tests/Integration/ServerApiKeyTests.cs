using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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
using Microsoft.Extensions.Options;
using Xunit;

namespace Deneblab.StashLock.Tests.Integration;

public class ServerApiKeyTests : IClassFixture<ServerApiKeyTests.ApiKeyFactory>
{
    private const string TestApiKey = "test-secret-key-12345";
    private readonly ApiKeyFactory _factory;

    public ServerApiKeyTests(ApiKeyFactory factory)
    {
        _factory = factory;
    }

    public class ApiKeyFactory : WebApplicationFactory<Program>
    {
        private static readonly string DbName = "ApiKeyTests-" + Guid.NewGuid();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                // Replace Registry
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

                // Configure API key for testing
                services.Configure<StashLockOptions>(opts =>
                {
                    opts.ApiKey = TestApiKey;
                });

                // Replace EF Core with InMemory
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

    private HttpClient CreateClientWithApiKey(string apiKey = TestApiKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    [Fact]
    public async Task GetBox_WithoutApiKey_ShouldReturn401()
    {
        // Arrange
        var client = _factory.CreateClient();
        var encodedKey = EncodeKey("auth.test.v1");

        // Act
        var response = await client.GetAsync($"/boxes/{encodedKey}");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetBox_WithWrongApiKey_ShouldReturn401()
    {
        // Arrange
        using var client = CreateClientWithApiKey("wrong-key");
        var encodedKey = EncodeKey("auth.test.v1");

        // Act
        var response = await client.GetAsync($"/boxes/{encodedKey}");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SetAndGetBox_WithValidApiKey_ShouldSucceed()
    {
        // Arrange
        using var client = CreateClientWithApiKey();
        var encodedKey = EncodeKey("auth.roundtrip.v1");
        var secretValue = "api key protected secret";

        // Act — Set
        var setResponse = await client.PostAsync(
            $"/boxes/{encodedKey}",
            new StringContent(secretValue, Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);

        // Act — Get
        var getResponse = await client.GetAsync($"/boxes/{encodedKey}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var retrieved = await getResponse.Content.ReadAsStringAsync();
        Assert.Equal(secretValue, retrieved);
    }

    [Fact]
    public async Task HealthCheck_WithoutApiKey_ShouldBypass()
    {
        // Arrange — no auth header
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/healthz");

        // Assert — healthz should always be accessible
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Root_WithoutApiKey_ShouldBypass()
    {
        // Arrange — no auth header
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/");

        // Assert — root health check should always be accessible
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("OK ; Version:", content);
    }

    [Fact]
    public async Task Unauthorized_ShouldReturnJsonApiError()
    {
        // Arrange
        var client = _factory.CreateClient();
        var encodedKey = EncodeKey("auth.test.v1");

        // Act
        var response = await client.GetAsync($"/boxes/{encodedKey}");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Unauthorized", body);
    }
}
