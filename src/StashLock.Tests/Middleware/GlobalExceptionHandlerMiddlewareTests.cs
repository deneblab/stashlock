using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Deneblab.StashLock.Server.Exceptions;
using Deneblab.StashLock.Server.Middleware;
using Deneblab.StashLock.Server.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using Xunit;

namespace Deneblab.StashLock.Tests.Middleware;

public class GlobalExceptionHandlerMiddlewareTests
{
    private readonly ILogger<GlobalExceptionHandlerMiddleware> _logger;

    public GlobalExceptionHandlerMiddlewareTests()
    {
        _logger = LoggerFactory.Create(builder => builder.AddNLog())
            .CreateLogger<GlobalExceptionHandlerMiddleware>();
    }

    private GlobalExceptionHandlerMiddleware CreateMiddleware(RequestDelegate next)
    {
        return new GlobalExceptionHandlerMiddleware(next, _logger);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<ApiError?> ReadErrorResponse(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return await JsonSerializer.DeserializeAsync<ApiError>(context.Response.Body);
    }

    [Fact]
    public async Task InvokeAsync_ShouldPassThroughWhenNoException()
    {
        // Arrange
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn400ForArgumentException()
    {
        // Arrange
        var middleware = CreateMiddleware(_ => throw new ArgumentException("Invalid input"));
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(400, context.Response.StatusCode);
        var error = await ReadErrorResponse(context);
        Assert.NotNull(error);
        Assert.Equal("Bad Request", error.Title);
        Assert.Contains("Invalid input", error.Detail);
        Assert.Equal(ErrorCodes.InvalidKeyFormat, error.ErrorCode);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn400ForFormatException()
    {
        // Arrange
        var middleware = CreateMiddleware(_ => throw new FormatException("Bad base64"));
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(400, context.Response.StatusCode);
        var error = await ReadErrorResponse(context);
        Assert.NotNull(error);
        Assert.Equal("Bad Request", error.Title);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn404ForKeyNotFoundException()
    {
        // Arrange
        var middleware = CreateMiddleware(_ => throw new KeyNotFoundException("Key not found"));
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(404, context.Response.StatusCode);
        var error = await ReadErrorResponse(context);
        Assert.NotNull(error);
        Assert.Equal("Not Found", error.Title);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn500ForUnexpectedException()
    {
        // Arrange
        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("Something broke"));
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(500, context.Response.StatusCode);
        var error = await ReadErrorResponse(context);
        Assert.NotNull(error);
        Assert.Equal("Internal Server Error", error.Title);
    }

    [Fact]
    public async Task InvokeAsync_ShouldSetJsonContentType()
    {
        // Arrange
        var middleware = CreateMiddleware(_ => throw new Exception("test"));
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal("application/json", context.Response.ContentType);
    }

    [Fact]
    public async Task InvokeAsync_ShouldIncludeTraceId()
    {
        // Arrange
        var middleware = CreateMiddleware(_ => throw new Exception("test"));
        var context = CreateHttpContext();
        context.TraceIdentifier = "test-trace-123";

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        var error = await ReadErrorResponse(context);
        Assert.NotNull(error);
        Assert.Equal("test-trace-123", error.TraceId);
    }

    [Fact]
    public async Task InvokeAsync_ShouldHandleStashLockException()
    {
        // Arrange
        var middleware = CreateMiddleware(_ =>
            throw new StashLockException(ErrorCodes.ValueTooLarge, "Too large", 400));
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(400, context.Response.StatusCode);
        var error = await ReadErrorResponse(context);
        Assert.NotNull(error);
        Assert.Equal("Bad Request", error.Title);
        Assert.Equal(ErrorCodes.ValueTooLarge, error.ErrorCode);
        Assert.Equal("Too large", error.Detail);
    }

    [Fact]
    public async Task InvokeAsync_ShouldOmitNullErrorsField()
    {
        // Arrange
        var middleware = CreateMiddleware(_ => throw new Exception("test"));
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var json = await reader.ReadToEndAsync();
        Assert.DoesNotContain("\"Errors\"", json);
    }
}
