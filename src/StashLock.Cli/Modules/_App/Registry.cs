using Deneblab.StashLock.Cli.Common.Simple;

namespace Deneblab.StashLock.Cli.Modules.App;

public class Registry
{
    public Registry(SimpleEnvResult env, SimpleVersionInfo appVersion)
    {
        Env = env;
        AppVersion = appVersion;
    }

    public SimpleEnvResult Env { get; }
    public SimpleVersionInfo AppVersion { get; }
}
