namespace Deneblab.StashLock.Client;

/// <summary>
/// Entry point for the StashLock client fluent API.
/// </summary>
public static class StashLock
{
    /// <summary>
    /// Creates a new StashLock client builder for configuring and opening a secrets store.
    /// </summary>
    /// <example>
    /// <code>
    /// // Uses STASHLOCK_CONNECTION_STRING when set
    /// var store = await StashLock.CreateClient().OpenAsync();
    ///
    /// var store = await StashLock.CreateClient()
    ///     .WithBox("myapp", "production")
    ///     .WithCache(ttl: TimeSpan.FromHours(2))
    ///     .OpenAsync();
    /// </code>
    /// </example>
    public static StashLockBuilder CreateClient()
    {
        return new StashLockBuilder();
    }
}
