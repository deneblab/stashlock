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

    private static StashLockConfigurationSource BuildConfigurationSource(StashLockBuilder slBuilder)
    {
        switch (slBuilder.Mode)
        {
            case StashLockBuilder.SourceMode.Box:
                return new StashLockConfigurationSource
                {
                    Mode = StashLockSourceMode.Remote,
                    Box = slBuilder.Box,
                    Tag = slBuilder.Tag,
                    Version = slBuilder.Version,
                    CacheOptions = slBuilder.CacheOpts,
                    PrivateKeyBase64 = slBuilder.PrivateKeyBase64,
                    ApiUrl = slBuilder.ApiUrl,
                    ApiKey = slBuilder.ApiKey
                };

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
                    FilePath = slBuilder.FilePath
                };

            default:
                throw new Common.Exceptions.StashLockException(
                    "AddStashLock requires a source. Call WithBox(), FromEncryptedFile(), or FromDevFile() in the configure lambda.");
        }
    }
}
