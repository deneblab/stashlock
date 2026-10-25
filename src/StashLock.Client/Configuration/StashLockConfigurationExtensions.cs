using System;
using Microsoft.Extensions.Configuration;

namespace Deneblab.StashLock.Client.Configuration;

/// <summary>
/// Extension methods for adding StashLock secrets to IConfigurationBuilder.
/// </summary>
public static class StashLockConfigurationExtensions
{
    /// <summary>
    /// Adds StashLock secrets to the configuration builder using a fluent lambda.
    /// The lambda receives a <see cref="StashLockBuilder"/> for configuration.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Configuration.AddStashLock(cfg => cfg
    ///     .WithBox("myapp", "prod")
    ///     .WithCache(ttl: TimeSpan.FromHours(2))
    /// );
    /// </code>
    /// </example>
    public static IConfigurationBuilder AddStashLock(
        this IConfigurationBuilder builder,
        Action<StashLockBuilder> configure)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (configure == null) throw new ArgumentNullException(nameof(configure));

        var slBuilder = new StashLockBuilder();
        configure(slBuilder);

        var source = BuildConfigurationSource(slBuilder);
        builder.Add(source);

        return builder;
    }

    // FromDevFile() without a path: same discovery as OpenAsync
    private static string FindDevFile()
    {
        var looked = SecretsStore.DevFileCandidates(Common.Simple.SimpleEnv.GitRoot(), Common.Simple.SimpleEnv.Detect().AppRoot);
        foreach (var path in looked)
            if (System.IO.File.Exists(path))
                return path;
        throw new Common.Exceptions.StashLockException(looked.Length == 0
            ? "FromDevFile(): not inside a git checkout. Pass a path: FromDevFile(path)."
            : $"FromDevFile(): no dev secrets file found. Looked in: {string.Join(", ", looked)}.");
    }

    private static StashLockConfigurationSource BuildConfigurationSource(StashLockBuilder slBuilder)
    {
        switch (slBuilder.Mode)
        {
            case StashLockBuilder.SourceMode.Box:
                return new StashLockConfigurationSource { Mode = StashLockSourceMode.Remote, Builder = slBuilder };

            case StashLockBuilder.SourceMode.EncryptedFile:
                return new StashLockConfigurationSource
                {
                    Mode = StashLockSourceMode.EncryptedFile,
                    FilePath = slBuilder.FilePath,
                    PrivateKeyBase64 = slBuilder.PrivateKeyBase64
                };

            case StashLockBuilder.SourceMode.DevFile:
            case StashLockBuilder.SourceMode.PlainFile:
                return new StashLockConfigurationSource
                {
                    Mode = StashLockSourceMode.DevFile,
                    FilePath = slBuilder.FilePath ?? FindDevFile()
                };

            default:
                // No explicit source: STASHLOCK_CONNECTION_STRING (read by OpenAsync) may provide Box and Tag
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STASHLOCK_CONNECTION_STRING")))
                    return new StashLockConfigurationSource { Mode = StashLockSourceMode.Remote, Builder = slBuilder };
                throw new Common.Exceptions.StashLockException(
                    "AddStashLock requires a source. Call WithConnectionString(), WithBox(), FromEncryptedFile() or FromDevFile() in the configure lambda, or set STASHLOCK_CONNECTION_STRING.");
        }
    }
}
