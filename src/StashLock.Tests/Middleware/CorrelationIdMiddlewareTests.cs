using System.IO;
using System.Threading.Tasks;
using Deneblab.StashLock.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Deneblab.StashLock.Tests.Middleware;

public class CorrelationIdMiddlewareTests
{
    private static CorrelationIdMiddleware CreateMiddleware(RequestDelegate next)
    {
        return new CorrelationIdMiddleware(next);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    [Fact]
    public async Task InvokeAsync_ShouldGenerateCorrelationIdWhenNotProvided()
    {
        // Arrange
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.NotEmpty(context.TraceIdentifier);
        Assert.True(context.Response.Headers.ContainsKey("X-Correlation-Id"));
        Assert.NotEmpty(context.Response.Headers["X-Correlation-Id"].ToString());
    }

    [Fact]
    public async Task InvokeAsync_ShouldEchoProvidedCorrelationId()
    {
        // Arrange
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateHttpContext();
        context.Request.Headers["X-Correlation-Id"] = "my-custom-id-123";

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal("my-custom-id-123", context.TraceIdentifier);
        Assert.Equal("my-custom-id-123", context.Response.Headers["X-Correlation-Id"].ToString());
    }

    [Fact]
    public async Task InvokeAsync_ShouldSetTraceIdentifier()
    {
        // Arrange
        string? capturedTraceId = null;
        var middleware = CreateMiddleware(ctx =>
        {
            capturedTraceId = ctx.TraceIdentifier;
            return Task.CompletedTask;
        });
        var context = CreateHttpContext();
        context.Request.Headers["X-Correlation-Id"] = "trace-test-456";

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal("trace-test-456", capturedTraceId);
    }
}
