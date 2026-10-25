namespace Deneblab.StashLock.Cli.Common;

/// <summary>Process exit codes returned by every command.</summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;

    /// <summary>Some items succeeded and some failed (e.g. publish of several files).</summary>
    public const int Partial = 2;
}
