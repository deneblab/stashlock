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

public class ScopedApiKeyTests : IClassFixture<ScopedApiKeyTests.ScopedKeyFactory>
{
    private const string MasterKey = "master-key-for-scoped-tests";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly ScopedKeyFactory _factory;

    public ScopedApiKeyTests(ScopedKeyFactory factory)
    {
        _factory = factory;
    }

    public class ScopedKeyFactory : WebApplicationFactory<Program>
    {
        private static readonly string DbName = "ScopedKeyTests-" + Guid.NewGuid();

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

    private async Task<string> CreateScopedKey(
        string name,
        List<string>? scopes = null,
        bool canRead = true,
        bool canWrite = true,
        bool canDelete = false)
    {
        using var master = CreateMasterClient();
        var request = new CreateApiKeyRequest
        {
            Name = name,
            Scopes = scopes ?? new List<string> { "*" },
            CanRead = canRead,
            CanWrite = canWrite,
            CanDelete = canDelete
        };

        var json = JsonSerializer.Serialize(request);
        var response = await master.PostAsync("/keys",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<CreateApiKeyResponse>(body, JsonOpts);
        return result!.Key;
    }

    // --- Key Management (master-only) ---

    [Fact]
    public async Task CreateKey_WithMaster_ShouldSucceed()
    {
        using var master = CreateMasterClient();
        var request = new CreateApiKeyRequest { Name = "create-test" };
        var json = JsonSerializer.Serialize(request);

        var response = await master.PostAsync("/keys",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<CreateApiKeyResponse>(body, JsonOpts);
        Assert.NotNull(result);
        Assert.StartsWith("slk_", result.Key);
        Assert.Equal("create-test", result.Info.Name);
        Assert.True(result.Info.IsActive);
    }

    [Fact]
    public async Task CreateKey_WithScopedKey_ShouldReturn403()
    {
        var scopedKey = await CreateScopedKey("no-admin");
        using var client = CreateClientWithKey(scopedKey);
        var request = new CreateApiKeyRequest { Name = "should-fail" };
        var json = JsonSerializer.Serialize(request);

        var response = await client.PostAsync("/keys",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListKeys_WithMaster_ShouldReturnKeys()
    {
        using var master = CreateMasterClient();
        await CreateScopedKey("list-test-1");
        await CreateScopedKey("list-test-2");

        var response = await master.GetAsync("/keys");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var keys = JsonSerializer.Deserialize<List<ApiKeyInfo>>(body, JsonOpts);
        Assert.NotNull(keys);
        Assert.True(keys.Count >= 2);
    }

    [Fact]
    public async Task ListKeys_WithScopedKey_ShouldReturn403()
    {
        var scopedKey = await CreateScopedKey("no-list-access");
        using var client = CreateClientWithKey(scopedKey);

        var response = await client.GetAsync("/keys");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RevokeKey_WithMaster_ShouldSucceed()
    {
        using var master = CreateMasterClient();
        var scopedKey = await CreateScopedKey("to-revoke");

        // Get the key ID first
        var listResponse = await master.GetAsync("/keys");
        var body = await listResponse.Content.ReadAsStringAsync();
        var keys = JsonSerializer.Deserialize<List<ApiKeyInfo>>(body, JsonOpts);
        var keyInfo = keys!.FirstOrDefault(k => k.Name == "to-revoke");
        Assert.NotNull(keyInfo);

        // Revoke
        var response = await master.DeleteAsync($"/keys/{keyInfo.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify revoked key no longer works
        using var client = CreateClientWithKey(scopedKey);
        var encodedKey = EncodeKey("revoke.test.v1");
        var getResponse = await client.GetAsync($"/boxes/{encodedKey}");
        Assert.Equal(HttpStatusCode.Unauthorized, getResponse.StatusCode);
    }

    // --- Permission enforcement ---

    [Fact]
    public async Task ScopedKey_CanRead_ShouldAccessBox()
    {
        // Store a value with master
        using var master = CreateMasterClient();
        var encodedKey = EncodeKey("perm.read.v1");
        await master.PostAsync($"/boxes/{encodedKey}",
            new StringContent("readable", Encoding.UTF8, "text/plain"));

        // Create read-only key
        var readKey = await CreateScopedKey("reader", canRead: true, canWrite: false, canDelete: false);
        using var reader = CreateClientWithKey(readKey);

        var response = await reader.GetAsync($"/boxes/{encodedKey}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("readable", content);
    }

    [Fact]
    public async Task ScopedKey_NoWritePermission_ShouldReturn403()
    {
        var readOnlyKey = await CreateScopedKey("readonly", canRead: true, canWrite: false, canDelete: false);
        using var client = CreateClientWithKey(readOnlyKey);
        var encodedKey = EncodeKey("perm.nowrite.v1");

        var response = await client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("try write", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("FORBIDDEN", body);
    }

    [Fact]
    public async Task ScopedKey_NoDeletePermission_ShouldReturn403()
    {
        // Store a value with master first
        using var master = CreateMasterClient();
        var encodedKey = EncodeKey("perm.nodelete.v1");
        await master.PostAsync($"/boxes/{encodedKey}",
            new StringContent("nodelete", Encoding.UTF8, "text/plain"));

        // Try delete with no-delete key
        var noDeleteKey = await CreateScopedKey("nodelete", canRead: true, canWrite: true, canDelete: false);
        using var client = CreateClientWithKey(noDeleteKey);

        var response = await client.DeleteAsync($"/boxes/{encodedKey}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ScopedKey_WithDeletePermission_ShouldDeleteBox()
    {
        // Store a value with master
        using var master = CreateMasterClient();
        var encodedKey = EncodeKey("perm.candelete.v1");
        await master.PostAsync($"/boxes/{encodedKey}",
            new StringContent("deletable", Encoding.UTF8, "text/plain"));

        // Delete with key that has delete permission
        var deleteKey = await CreateScopedKey("deleter", canRead: true, canWrite: false, canDelete: true);
        using var client = CreateClientWithKey(deleteKey);

        var response = await client.DeleteAsync($"/boxes/{encodedKey}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- Scope enforcement ---

    [Fact]
    public async Task ScopedKey_WithBoxScope_ShouldAccessScopedBox()
    {
        // Store a value in "app1" box
        using var master = CreateMasterClient();
        var encodedKey = EncodeKey("app1.config.v1");
        await master.PostAsync($"/boxes/{encodedKey}",
            new StringContent("app1 secret", Encoding.UTF8, "text/plain"));

        // Create key scoped to app1
        var scopedKey = await CreateScopedKey("app1-reader",
            scopes: new List<string> { "app1" }, canRead: true, canWrite: false);
        using var client = CreateClientWithKey(scopedKey);

        var response = await client.GetAsync($"/boxes/{encodedKey}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("app1 secret", content);
    }

    [Fact]
    public async Task ScopedKey_WithBoxScope_ShouldNotAccessOtherBox()
    {
        // Store a value in "app2" box
        using var master = CreateMasterClient();
        var encodedKey = EncodeKey("app2.config.v1");
        await master.PostAsync($"/boxes/{encodedKey}",
            new StringContent("app2 secret", Encoding.UTF8, "text/plain"));

        // Create key scoped to app1 only
        var scopedKey = await CreateScopedKey("app1-only",
            scopes: new List<string> { "app1" }, canRead: true, canWrite: true);
        using var client = CreateClientWithKey(scopedKey);

        var response = await client.GetAsync($"/boxes/{encodedKey}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ScopedKey_WithWildcardScope_ShouldAccessAnyBox()
    {
        // Store a value
        using var master = CreateMasterClient();
        var encodedKey = EncodeKey("anybox.test.v1");
        await master.PostAsync($"/boxes/{encodedKey}",
            new StringContent("wildcard", Encoding.UTF8, "text/plain"));

        // Create key with wildcard scope
        var wildcardKey = await CreateScopedKey("wildcard-reader",
            scopes: new List<string> { "*" }, canRead: true, canWrite: false);
        using var client = CreateClientWithKey(wildcardKey);

        var response = await client.GetAsync($"/boxes/{encodedKey}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ScopedKey_WriteToScopedBox_ShouldSucceed()
    {
        var scopedKey = await CreateScopedKey("app1-writer",
            scopes: new List<string> { "scopewr" }, canRead: true, canWrite: true);
        using var client = CreateClientWithKey(scopedKey);
        var encodedKey = EncodeKey("scopewr.secret.v1");

        var response = await client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("scoped write", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ScopedKey_WriteToOutOfScopeBox_ShouldReturn403()
    {
        var scopedKey = await CreateScopedKey("limited-writer",
            scopes: new List<string> { "allowed" }, canRead: true, canWrite: true);
        using var client = CreateClientWithKey(scopedKey);
        var encodedKey = EncodeKey("forbidden.secret.v1");

        var response = await client.PostAsync($"/boxes/{encodedKey}",
            new StringContent("out of scope write", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- List boxes with scoped key ---

    [Fact]
    public async Task ScopedKey_ListBoxes_ShouldWorkWithReadPermission()
    {
        var scopedKey = await CreateScopedKey("lister", canRead: true, canWrite: false);
        using var client = CreateClientWithKey(scopedKey);

        var response = await client.GetAsync("/boxes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ScopedKey_NoReadPermission_ListBoxesShouldReturn403()
    {
        var writeOnlyKey = await CreateScopedKey("writeonly", canRead: false, canWrite: true);
        using var client = CreateClientWithKey(writeOnlyKey);

        var response = await client.GetAsync("/boxes");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- Master key preserves full access ---

    [Fact]
    public async Task MasterKey_ShouldHaveFullAccess()
    {
        using var master = CreateMasterClient();
        var encodedKey = EncodeKey("master.full.v1");

        // Write
        var setResponse = await master.PostAsync($"/boxes/{encodedKey}",
            new StringContent("master data", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);

        // Read
        var getResponse = await master.GetAsync($"/boxes/{encodedKey}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        // Delete
        var deleteResponse = await master.DeleteAsync($"/boxes/{encodedKey}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        // Manage keys
        var listKeysResponse = await master.GetAsync("/keys");
        Assert.Equal(HttpStatusCode.OK, listKeysResponse.StatusCode);
    }
}
