using System;
using Microsoft.Extensions.DependencyInjection;

namespace Deneblab.StashLock.Client.Configuration;

/// <summary>
/// Extension methods for registering StashLock in a DI container.
/// </summary>
public static class StashLockServiceExtensions
{
    /// <summary>
    /// Registers <see cref="ISecretsStore"/> as a singleton in the service collection.
    /// The store is opened once at first resolution and reused for the application lifetime.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddStashLock(cfg => cfg
    ///     .WithBox("myapp", "prod")
    ///     .WithCache(ttl: TimeSpan.FromHours(2))
    /// );
    /// </code>
    /// </example>
    public static IServiceCollection AddStashLock(
        this IServiceCollection services,
        Action<StashLockBuilder> configure)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));
        if (configure == null) throw new ArgumentNullException(nameof(configure));

        services.AddSingleton<ISecretsStore>(sp =>
        {
            var builder = new StashLockBuilder();
            configure(builder);
            return builder.OpenAsync().GetAwaiter().GetResult();
        });

        return services;
    }
}
