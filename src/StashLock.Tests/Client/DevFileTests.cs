using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Deneblab.StashLock.Client;
using Deneblab.StashLock.Client.Common.Exceptions;
using Xunit;

namespace Deneblab.StashLock.Tests.Client;

public class DevFileTests : System.IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("stashlock-devfile-").FullName;

    public void Dispose() => Directory.Delete(_repo, true);

    private string Write(string relative, string json)
    {
        var path = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    private string[] Candidates() => SecretsStore.DevFileCandidates(_repo, Path.Combine(_repo, "dev", "app.vs"));

    [Fact]
    public void Candidates_RepoRootFirst_ThenAppRoot()
    {
        var c = Candidates();
        Assert.Equal(Path.Combine(_repo, "dev", "secrets", "secrets.json"), c[0]);
        Assert.Equal(Path.Combine(_repo, "dev", "app.vs", "dev", "secrets", "secrets.json"), c[1]);
    }

    [Fact]
    public async Task FindsDocumentedPath()
    {
        Write("dev/secrets/secrets.json", "{\"Db\":\"local\"}");
        var store = await SecretsStore.TryOpenDevFileAsync(Candidates(), CancellationToken.None);
        Assert.Equal("local", store!["Db"]);
    }

    [Fact]
    public async Task FallsBackToAppRoot()
    {
        Write("dev/app.vs/dev/secrets/secrets.json", "{\"Db\":\"app\"}");
        var store = await SecretsStore.TryOpenDevFileAsync(Candidates(), CancellationToken.None);
        Assert.Equal("app", store!["Db"]);
    }

    [Fact]
    public async Task MissingFile_ReturnsNull()
    {
        Assert.Null(await SecretsStore.TryOpenDevFileAsync(Candidates(), CancellationToken.None));
    }

    [Fact]
    public async Task InvalidJson_ThrowsWithFileName()
    {
        var path = Write("dev/secrets/secrets.json", "{ not json");
        var ex = await Assert.ThrowsAsync<StashLockException>(() => SecretsStore.TryOpenDevFileAsync(Candidates(), CancellationToken.None));
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public async Task OpenFile_AcceptsNestedObjectsAndArrays()
    {
        var flat = Write("a.json", "{\"Db\":{\"Password\":\"p\",\"Port\":5432},\"Hosts\":[\"h1\",\"h2\"],\"On\":true}");
        var store = await SecretsStore.OpenFileAsync(flat);
        Assert.Equal("p", store["Db:Password"]);
        Assert.Equal("5432", store["Db:Port"]);
        Assert.Equal("h2", store["Hosts:1"]);
        Assert.Equal("true", store["On"]);
    }
}
