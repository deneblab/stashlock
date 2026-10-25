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
        Assert.Equal("slk_flag", ServerSettings.ResolveApiKey("slk_flag"));
        Assert.Equal("slk_env", ServerSettings.ResolveApiKey(""));

        Environment.SetEnvironmentVariable(ServerSettings.ApiKeyEnvVar, null);
        Assert.Null(ServerSettings.ResolveApiKey(""));
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
}
