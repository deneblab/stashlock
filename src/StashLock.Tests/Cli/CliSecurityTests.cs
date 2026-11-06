using System;
using System.IO;
using Deneblab.StashLock.Cli.Common.Security;
using Deneblab.StashLock.Cli.Models;
using Xunit;

namespace Deneblab.StashLock.Tests.Cli;

[CollectionDefinition(nameof(EnvironmentVariablesCollection), DisableParallelization = true)]
public class EnvironmentVariablesCollection;

[Collection(nameof(EnvironmentVariablesCollection))]
public class ServerSettingsTests : IDisposable
{
    private readonly string? _url = Environment.GetEnvironmentVariable(ServerSettings.UrlEnvVar);
    private readonly string? _key = Environment.GetEnvironmentVariable(ServerSettings.ApiKeyEnvVar);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ServerSettings.UrlEnvVar, _url);
        Environment.SetEnvironmentVariable(ServerSettings.ApiKeyEnvVar, _key);
    }

    [Fact]
    public void ResolveUrl_PrefersFlagThenEnvThenConfig()
    {
        var config = new StashlockConfig { ServerUrl = "https://config" };

        Environment.SetEnvironmentVariable(ServerSettings.UrlEnvVar, "https://env");
        Assert.Equal("https://flag", ServerSettings.ResolveUrl("https://flag", config));
        Assert.Equal("https://env", ServerSettings.ResolveUrl("", config));

        Environment.SetEnvironmentVariable(ServerSettings.UrlEnvVar, null);
        Assert.Equal("https://config", ServerSettings.ResolveUrl("", config));
    }

    [Fact]
    public void ResolveUrl_ReturnsNullWithoutAnySource()
    {
        Environment.SetEnvironmentVariable(ServerSettings.UrlEnvVar, null);
        Assert.Null(ServerSettings.ResolveUrl("", new StashlockConfig()));
        Assert.Null(ServerSettings.ResolveUrl(null, null));
    }

    [Fact]
    public void ResolveApiKey_PrefersFlagThenEnv()
    {
        Environment.SetEnvironmentVariable(ServerSettings.ApiKeyEnvVar, "slk_env");
        Assert.Equal(new ResolvedApiKey("slk_flag", ApiKeySource.Flag), ServerSettings.ResolveApiKey("slk_flag"));
        Assert.Equal(new ResolvedApiKey("slk_env", ApiKeySource.Environment), ServerSettings.ResolveApiKey(""));

        Environment.SetEnvironmentVariable(ServerSettings.ApiKeyEnvVar, null);
        Assert.Null(ServerSettings.ResolveApiKey(""));
    }

    [Fact]
    public void ResolveApiKey_PrefersFlagThenEnvThenConfigTag()
    {
        var config = new StashlockConfig { ApiKeys = new() { ["dev"] = "slk_config" } };

        Environment.SetEnvironmentVariable(ServerSettings.ApiKeyEnvVar, "slk_env");
        Assert.Equal("slk_flag", ServerSettings.ResolveApiKey("slk_flag", config, "dev")!.Value);
        Assert.Equal("slk_env", ServerSettings.ResolveApiKey("", config, "dev")!.Value);

        Environment.SetEnvironmentVariable(ServerSettings.ApiKeyEnvVar, null);
        Assert.Equal(new ResolvedApiKey("slk_config", ApiKeySource.Config), ServerSettings.ResolveApiKey("", config, "dev"));
        Assert.Equal("slk_config", ServerSettings.ResolveApiKey("", config, "DEV")!.Value);
    }

    [Fact]
    public void ResolveApiKey_TagMissingOrBlankInConfig_ReturnsNull()
    {
        Environment.SetEnvironmentVariable(ServerSettings.ApiKeyEnvVar, null);
        var config = new StashlockConfig { ApiKeys = new() { ["dev"] = "slk_dev", ["prod"] = " " } };

        Assert.Null(ServerSettings.ResolveApiKey("", config, "staging"));
        Assert.Null(ServerSettings.ResolveApiKey("", config, "prod"));
        Assert.Null(ServerSettings.ResolveApiKey("", config, null));
        Assert.Null(ServerSettings.ResolveApiKey("", new StashlockConfig(), "dev"));
    }
}

public class SecureFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("stashlock-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void WriteAllText_CreatesOwnerOnlyFileOnUnix()
    {
        var path = Path.Combine(_dir, "stashlock.key.dev.json");
        SecureFile.WriteAllText(path, "{\"PrivateKey\":\"x\"}");

        Assert.Equal("{\"PrivateKey\":\"x\"}", File.ReadAllText(path));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void WriteAllText_ReplacesExistingFileAndTightensMode()
    {
        var path = Path.Combine(_dir, "out.json");
        File.WriteAllText(path, "old content that is longer");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        SecureFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}

public class GitIgnoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("stashlock-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void EnsureKeyFilesIgnored_CreatesFile()
    {
        Assert.True(GitIgnore.EnsureKeyFilesIgnored(_dir));
        Assert.Contains(GitIgnore.KeyFilePattern, File.ReadAllText(Path.Combine(_dir, ".gitignore")));
    }

    [Fact]
    public void EnsureKeyFilesIgnored_AppendsOnceToExistingFile()
    {
        var path = Path.Combine(_dir, ".gitignore");
        File.WriteAllText(path, "bin/");

        Assert.True(GitIgnore.EnsureKeyFilesIgnored(_dir));
        Assert.False(GitIgnore.EnsureKeyFilesIgnored(_dir));

        var lines = File.ReadAllLines(path);
        Assert.Equal("bin/", lines[0]);
        Assert.Single(lines, l => l == GitIgnore.KeyFilePattern);
    }

    [Fact]
    public void EnsureConfigIgnored_AddsBothPatternsOnceEach()
    {
        Assert.False(GitIgnore.IsIgnored(_dir, GitIgnore.ConfigFilePattern));

        Assert.True(GitIgnore.EnsureKeyFilesIgnored(_dir));
        Assert.True(GitIgnore.EnsureConfigIgnored(_dir));
        Assert.False(GitIgnore.EnsureConfigIgnored(_dir));
        Assert.False(GitIgnore.EnsureKeyFilesIgnored(_dir));

        var lines = File.ReadAllLines(Path.Combine(_dir, ".gitignore"));
        Assert.Single(lines, l => l == GitIgnore.KeyFilePattern);
        Assert.Single(lines, l => l == GitIgnore.ConfigFilePattern);
        Assert.True(GitIgnore.IsIgnored(_dir, GitIgnore.ConfigFilePattern));
    }
}
