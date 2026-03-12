using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Deneblab.StashLock.Cli.Common.Simple;

public abstract class SimpleRunner
{
    public void LogCritical(SimpleEnvResult env, SimpleVersionInfo appVersion, string appName, Exception exception)
    {
        var l = new List<string>();
        if (!Directory.Exists(env.LogDir)) Directory.CreateDirectory(env.LogDir);

        var logFilePath = Path.Combine(env.LogDir, $"{appName}.CRITICAL.log");
        if (File.Exists(logFilePath))
        {
            l.Add("");
            l.Add("************************************* ");
            l.Add("");
        }

        l.Add("App:");
        l.Add($"{appName} {appVersion.SemVer}");
        l.Add("");
        l.Add("Date:");
        l.Add(DateTime.UtcNow.ToString("s"));
        l.Add("");
        l.Add("ExceptionType:");
        l.Add($"{exception.GetType()}");
        l.Add("");
        l.Add("ExceptionMessage:");
        l.Add($"{exception.Message}");
        l.Add("");
        l.Add("ExceptionStackTrace:");
        l.Add($"{exception.StackTrace}");
        l.Add("");
        l.Add("SimpleEnv:");
        l.Add($"{JsonSerializer.Serialize(env, new JsonSerializerOptions { WriteIndented = true })}");
        l.Add("");
        File.AppendAllLines(logFilePath, l);

        Console.WriteLine("CRITICAL ERROR");
        Console.WriteLine(exception);
    }
}
