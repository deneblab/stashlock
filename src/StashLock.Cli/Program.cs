using System;
using System.Threading.Tasks;
using ConsoleAppFramework;
using Deneblab.StashLock.Cli.Common.Simple;
using Deneblab.StashLock.Cli.Modules.App;
using Deneblab.StashLock.Cli.Modules.Decode;
using Deneblab.StashLock.Cli.Modules.Encode;
using Deneblab.StashLock.Cli.Modules.Init;
using Deneblab.StashLock.Cli.Modules.CString;
using Deneblab.StashLock.Cli.Modules.Keygen;
using Deneblab.StashLock.Cli.Modules.Publish;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Targets;

namespace Deneblab.StashLock.Cli;

internal class Program
{
    private static async Task Main(string[] args)
    {
        using var runner = new Runner();
        await runner.Run(args);
    }
}

internal sealed class Runner : SimpleRunner, IDisposable
{
    private readonly SimpleVersionInfo _appVersion;
    private readonly SimpleEnvResult _env;
    private readonly ILoggerFactory _factory;
    private bool _disposed;

    public Runner()
    {
        _env = SimpleEnv.Detect("Deneblab", "StashLock", SimpleAppMode.CurrentUserDir);
        _appVersion = SimpleVersionParser.FromCurrentApp();
        _factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Critical);
            builder.AddNLog();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _factory.Dispose();
        _disposed = true;
    }

    public async Task Run(string[] args)
    {
        try
        {
            var shouldContinue = StartupCheck(args, _appVersion);
            if (!shouldContinue) return;

            var reg = new Registry(_env, _appVersion);

            var nlogConfig = new LoggingConfiguration();
            var consoleTarget = new ColoredConsoleTarget("console")
            {
                Layout = "${longdate}|${level:uppercase=true}|${logger}|${message}${onexception:${newline}${exception:format=tostring}}"
            };
            var fileTarget = new FileTarget("file")
            {
                FileName = System.IO.Path.Combine(reg.Env.LogDir, "stashlock-cli.log"),
                Layout = "${longdate}|${level:uppercase=true}|${logger}|${message}${onexception:${newline}${exception:format=tostring}}",
                ArchiveAboveSize = 10_000_000,
                MaxArchiveFiles = 3
            };
            nlogConfig.AddTarget(consoleTarget);
            nlogConfig.AddTarget(fileTarget);
            nlogConfig.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Fatal, consoleTarget);
            nlogConfig.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, fileTarget);
            LogManager.Configuration = nlogConfig;

            var app = ConsoleApp.Create()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(reg);
                })
                .ConfigureLogging(x =>
                {
                    x.ClearProviders();
                    x.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
                    x.AddNLog();
                });

            app.Add<InitCommands>("init");
            app.Add<KeyCommands>("keygen");
            app.Add<GenerateCommands>("encode");
            app.Add<CstringCommands>("cstring");
            app.Add<DecodeCommands>("decode");
            app.Add<PublishCommands>("publish");

            await app.RunAsync(args);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            LogCritical(_env, _appVersion, "stashlock-cli", e);
        }
    }

    private static bool StartupCheck(string[] args, SimpleVersionInfo appVersion)
    {
        var msg = $"DenebLab StashLock CLI {appVersion.SemVer}";
        ConsoleApp.Version = msg;
        switch (args.Length)
        {
            case 0:
                break;
            case 1 when args[0].Equals("--version", StringComparison.OrdinalIgnoreCase):
                Console.WriteLine(msg);
                return false;
            case 1 when args[0].Equals("-h", StringComparison.OrdinalIgnoreCase):
                Console.WriteLine($"{msg}\n");
                return true;
            case 1 when args[0].Equals("--help", StringComparison.OrdinalIgnoreCase):
                Console.WriteLine($"{msg}\n");
                return true;
        }

        return true;
    }
}
