using System;
using System.IO;
using System.Threading.Tasks;
using Deneblab.StashLock.Server.Common.Simple;
using Deneblab.StashLock.Server.Data;
using Deneblab.StashLock.Server.Models;
using Deneblab.StashLock.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Deneblab.StashLock.Tests.Services;

public class DatabaseHealthCheckTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly DatabaseHealthCheck _healthCheck;

    public DatabaseHealthCheckTests()
    {
        var env = new SimpleEnvResult(
            SimpleAppMode.Test,
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath()
        );
        var version = new SimpleVersionInfo(
            "TestProduct",
            "1.0.0-test",
            0,
            "test",
            DateTime.UtcNow,
            "test",
            "abc123",
            0,
            new Dictionary<string, string>()
        );
        var registry = new Registry(env, version);

        var services = new ServiceCollection();
        services.AddSingleton(registry);
        services.AddDbContext<StashLockDbContext>(options =>
            options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()));

        _serviceProvider = services.BuildServiceProvider();
        _healthCheck = new DatabaseHealthCheck(_serviceProvider.GetRequiredService<IServiceScopeFactory>());
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnHealthyWhenDatabaseIsReachable()
    {
        // Act
        var result = await _healthCheck.CheckHealthAsync(new HealthCheckContext());

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("Database is reachable", result.Description);
    }
}
