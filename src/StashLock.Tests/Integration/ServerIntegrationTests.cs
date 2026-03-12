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

public class ServerIntegrationTests : IClassFixture<ServerIntegrationTests.TestFactory>
{
    private readonly HttpClient _client;

    public ServerIntegrationTests(TestFactory factory)
    {
        _client = factory.CreateClient();
    }

    public class TestFactory : WebApplicationFactory<Program>
    {
        private static readonly string DbName = "IntegrationTests-" + Guid.NewGuid();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                // Replace Registry with test-safe version
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

                // Remove all EF Core / DbContext registrations to avoid dual-provider conflict
                var dbContextDescriptors = services.Where(
                    d => d.ServiceType == typeof(DbContextOptions<StashLockDbContext>)
                      || d.ServiceType == typeof(StashLockDbContext)
                      || d.ServiceType.FullName?.Contains("EntityFrameworkCore") == true
                ).ToList();
                foreach (var d in dbContextDescriptors) services.Remove(d);

                // Re-add with InMemory provider — shared DB name for roundtrip tests
                services.AddDbContext<StashLockDbContext>((sp, options) =>
                {
                    options.UseInMemoryDatabase(DbName);
                });
            });
        }
    }

    private static string EncodeKey(string key) => Helpers.Base64UrlEncode(key);

    [Fact]
    public async Task GetRoot_ShouldReturnOkWithVersion()
    {
        // Act
        var response = await _client.GetAsync("/");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("OK ; Version:", content);
    }

    [Fact]
    public async Task HealthCheck_ShouldReturnHealthy()
    {
        // Act
        var response = await _client.GetAsync("/healthz");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SetAndGetBox_ShouldRoundTrip()
    {
        // Arrange
        var encodedKey = EncodeKey("inttest.roundtrip.v1");
        var secretValue = "integration test secret";

        // Act — Set
        var setResponse = await _client.PostAsync(
            $"/boxes/{encodedKey}",
            new StringContent(secretValue, Encoding.UTF8, "text/plain"));

        // Assert — Set
        Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);

        // Act — Get
        var getResponse = await _client.GetAsync($"/boxes/{encodedKey}");

        // Assert — Get
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var retrieved = await getResponse.Content.ReadAsStringAsync();
        Assert.Equal(secretValue, retrieved);
    }

    [Fact]
    public async Task GetBox_ShouldReturn404ForMissingKey()
    {
        // Arrange
        var encodedKey = EncodeKey("missing.key.v1");

        // Act
        var response = await _client.GetAsync($"/boxes/{encodedKey}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SetBox_ShouldReturn400ForInvalidBase64Key()
    {
        // Act
        var response = await _client.PostAsync(
            "/boxes/!!!invalid!!!",
            new StringContent("value", Encoding.UTF8, "text/plain"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SetBox_ShouldReturn400ForMalformedKey()
    {
        // Arrange — key with only 2 parts
        var encodedKey = EncodeKey("onlytwo.parts");

        // Act
        var response = await _client.PostAsync(
            $"/boxes/{encodedKey}",
            new StringContent("value", Encoding.UTF8, "text/plain"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ErrorResponse_ShouldReturnJsonWithApiError()
    {
        // Arrange
        var encodedKey = EncodeKey("missing.key.v1");

        // Act
        var response = await _client.GetAsync($"/boxes/{encodedKey}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Not Found", body);
    }

    [Fact]
    public async Task ErrorResponse_ShouldIncludeErrorCode()
    {
        // Arrange
        var encodedKey = EncodeKey("missing.key.v1");

        // Act
        var response = await _client.GetAsync($"/boxes/{encodedKey}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("KEY_NOT_FOUND", body);
    }

    [Fact]
    public async Task Response_ShouldIncludeCorrelationIdHeader()
    {
        // Act
        var response = await _client.GetAsync("/");

        // Assert
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
        var correlationId = response.Headers.GetValues("X-Correlation-Id");
        Assert.NotEmpty(correlationId);
    }

    [Fact]
    public async Task Response_ShouldEchoProvidedCorrelationId()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Correlation-Id", "test-correlation-789");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
        var values = response.Headers.GetValues("X-Correlation-Id");
        Assert.Contains("test-correlation-789", values);
    }

    [Fact]
    public async Task DeleteAndGet_ShouldReturn404()
    {
        // Arrange
        var encodedKey = EncodeKey("inttest.softdel.v1");
        await _client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("soft delete me", Encoding.UTF8, "text/plain"));

        // Act — Delete
        var deleteResponse = await _client.DeleteAsync($"/boxes/{encodedKey}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        // Act — Get after delete
        var getResponse = await _client.GetAsync($"/boxes/{encodedKey}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteAndSet_ShouldRestoreKey()
    {
        // Arrange
        var encodedKey = EncodeKey("inttest.restore.v1");
        await _client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("original value", Encoding.UTF8, "text/plain"));

        // Act — Delete then re-set
        await _client.DeleteAsync($"/boxes/{encodedKey}");
        await _client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("restored value", Encoding.UTF8, "text/plain"));

        // Act — Get
        var getResponse = await _client.GetAsync($"/boxes/{encodedKey}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var content = await getResponse.Content.ReadAsStringAsync();
        Assert.Equal("restored value", content);
    }

    [Fact]
    public async Task GetMetadata_ShouldReturnJsonForExistingKey()
    {
        // Arrange
        var encodedKey = EncodeKey("inttest.meta.v1");
        await _client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("metadata test", Encoding.UTF8, "text/plain"));

        // Act
        var response = await _client.GetAsync($"/boxes/{encodedKey}/meta");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var meta = JsonSerializer.Deserialize<KeyMetadata>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(meta);
        Assert.Equal(13, meta.SizeBytes);
        Assert.False(meta.IsDeleted);
    }

    [Fact]
    public async Task GetMetadata_ShouldReturn404ForMissingKey()
    {
        // Arrange
        var encodedKey = EncodeKey("missing.meta.v1");

        // Act
        var response = await _client.GetAsync($"/boxes/{encodedKey}/meta");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetMetadata_ShouldShowDeletedState()
    {
        // Arrange
        var encodedKey = EncodeKey("inttest.metadel.v1");
        await _client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("to delete", Encoding.UTF8, "text/plain"));
        await _client.DeleteAsync($"/boxes/{encodedKey}");

        // Act
        var response = await _client.GetAsync($"/boxes/{encodedKey}/meta");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var meta = JsonSerializer.Deserialize<KeyMetadata>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(meta);
        Assert.True(meta.IsDeleted);
        Assert.NotNull(meta.DeletedAt);
    }

    [Fact]
    public async Task ListBoxes_ShouldReturnJsonArray()
    {
        // Arrange
        var key1 = EncodeKey("intlist.one.v1");
        var key2 = EncodeKey("intlist.two.v1");
        await _client.PostAsync($"/boxes/{key1}", new StringContent("v1", Encoding.UTF8, "text/plain"));
        await _client.PostAsync($"/boxes/{key2}", new StringContent("v2", Encoding.UTF8, "text/plain"));

        // Act
        var response = await _client.GetAsync("/boxes?prefix=intlist");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var keys = JsonSerializer.Deserialize<List<KeyEntry>>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(keys);
        Assert.Equal(2, keys.Count);
    }

    [Fact]
    public async Task SetBox_ShouldReturnStructuredJson()
    {
        // Arrange
        var encodedKey = EncodeKey("intjson.test.v1");

        // Act
        var response = await _client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("json test", Encoding.UTF8, "text/plain"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResult>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(result);
        Assert.Equal("ok", result.Status);
        Assert.Equal("intjson.test.v1", result.Key);
    }

    [Fact]
    public async Task DeleteBox_ShouldReturnStructuredJson()
    {
        // Arrange
        var encodedKey = EncodeKey("intdeljson.test.v1");
        await _client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("to delete", Encoding.UTF8, "text/plain"));

        // Act
        var response = await _client.DeleteAsync($"/boxes/{encodedKey}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResult>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(result);
        Assert.Equal("ok", result.Status);
        Assert.Equal("intdeljson.test.v1", result.Key);
        Assert.Equal("Deleted", result.Message);
    }

    [Fact]
    public async Task SwaggerEndpoint_ShouldBeAccessible()
    {
        // Act
        var response = await _client.GetAsync("/swagger/v1/swagger.json");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("StashLock API", body);
    }

    // --- TTL / Expiration integration tests ---

    [Fact]
    public async Task SetBoxWithTtl_ShouldRoundTrip()
    {
        // Arrange
        var encodedKey = EncodeKey("intttl.roundtrip.v1");

        // Act — Set with 1-hour TTL
        var setResponse = await _client.PostAsync(
            $"/boxes/{encodedKey}?expiresIn=3600",
            new StringContent("ttl value", Encoding.UTF8, "text/plain"));

        // Assert — Set succeeded
        Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);

        // Act — Get
        var getResponse = await _client.GetAsync($"/boxes/{encodedKey}");

        // Assert — value is retrievable
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var content = await getResponse.Content.ReadAsStringAsync();
        Assert.Equal("ttl value", content);
    }

    [Fact]
    public async Task SetBoxWithTtl_ZeroReturns400()
    {
        // Arrange
        var encodedKey = EncodeKey("intttl.zero.v1");

        // Act
        var response = await _client.PostAsync(
            $"/boxes/{encodedKey}?expiresIn=0",
            new StringContent("bad ttl", Encoding.UTF8, "text/plain"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_TTL", body);
    }

    [Fact]
    public async Task SetBoxWithTtl_NegativeReturns400()
    {
        // Arrange
        var encodedKey = EncodeKey("intttl.neg.v1");

        // Act
        var response = await _client.PostAsync(
            $"/boxes/{encodedKey}?expiresIn=-5",
            new StringContent("bad ttl", Encoding.UTF8, "text/plain"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetMetadata_ShouldExposeExpiresAt()
    {
        // Arrange
        var encodedKey = EncodeKey("intttl.meta.v1");
        await _client.PostAsync(
            $"/boxes/{encodedKey}?expiresIn=7200",
            new StringContent("ttl meta", Encoding.UTF8, "text/plain"));

        // Act
        var response = await _client.GetAsync($"/boxes/{encodedKey}/meta");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var meta = JsonSerializer.Deserialize<KeyMetadata>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(meta);
        Assert.NotNull(meta.ExpiresAt);
        Assert.False(meta.IsExpired);
    }
}
