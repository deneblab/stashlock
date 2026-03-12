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
    /// var store = await StashLock.CreateClient()
    ///     .FromEnvironment()
    ///     .OpenAsync();
    /// </code>
    /// </example>
    public static StashLockBuilder CreateClient()
    {
        return new StashLockBuilder();
    }
}
