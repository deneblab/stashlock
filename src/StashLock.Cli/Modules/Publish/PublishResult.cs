namespace Deneblab.StashLock.Cli.Modules.Publish;

internal class PublishResult
{
    public bool IsSuccess { get; init; }
    public int StatusCode { get; init; }
    public string? ServerKey { get; init; }
    public string? Message { get; init; }
    public string? ErrorDetail { get; init; }

    public static PublishResult Success(string? serverKey, string? message) => new()
    {
        IsSuccess = true,
        StatusCode = 200,
        ServerKey = serverKey,
        Message = message
    };

    public static PublishResult Failure(int statusCode, string? errorDetail) => new()
    {
        IsSuccess = false,
        StatusCode = statusCode,
        ErrorDetail = errorDetail
    };
}
