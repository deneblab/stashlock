using System.IO;
using System.Threading.Tasks;
using Deneblab.StashLock.Cli.Common;
using Deneblab.StashLock.Cli.Modules.Init;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Deneblab.StashLock.Tests.Cli;

[Collection(nameof(EnvironmentVariablesCollection))]
public class InitNameTests : System.IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("stashlock-init-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static string ConfigName(string vault) => VaultConfig.Load(vault)!.Name;

    [Fact]
    public async Task Name_ComesFromTargetDirectory_NotCurrentDirectory()
    {
        var vault = Path.Combine(_dir, "ExampleApp");
        Assert.Equal(ExitCodes.Ok, await new InitCommands(NullLogger<InitCommands>.Instance).Root(vault, "dev"));
        Assert.Equal("exampleapp", ConfigName(vault));
    }

    [Fact]
    public async Task NameOption_Overrides()
    {
        var vault = Path.Combine(_dir, "secrets");
        Assert.Equal(ExitCodes.Ok, await new InitCommands(NullLogger<InitCommands>.Instance).Root(vault, "dev", name: "billing-api"));
        Assert.Equal("billing-api", ConfigName(vault));
    }
}
