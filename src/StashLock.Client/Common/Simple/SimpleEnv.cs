using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Deneblab.StashLock.Client.Common.Simple;

/// <summary>
///     Indicates how the application environment root is determined.
/// </summary>
public enum SimpleAppMode
{
    Dev = 20, // Running from a git repo (your convention below)
    Test = 30, // Running under test host (dotnet test, vstest, xUnit, etc.)
    DotnetTool = 300, // Running as a dotnet global/local tool
    Docker = 400, // Running inside a container
    ProcessPathDir = 500, // Based on AppContext.BaseDirectory
    CurrentUserDir = 600, // Current working directory
    UserRoot = 1000, // Found via custom markers in user’s dir tree
    CustomRoot = 1100 // Explicit custom root provided by caller
}

/// <summary>
///     Holds resolved environment information for an app.
/// </summary>
public sealed class SimpleEnvResult
{
    public SimpleEnvResult(SimpleAppMode appMode, string appRoot, string configDir, string logDir)
    {
        AppMode = appMode;
        AppRoot = appRoot;
        ConfigDir = configDir;
        LogDir = logDir;
    }

    public SimpleAppMode AppMode { get; }
    public string AppRoot { get; }
    public string ConfigDir { get; }
    public string LogDir { get; }
}

/// <summary>
///     Utility for detecting app runtime environment and determining root/config/log directories.
/// </summary>
public static class SimpleEnv
{
    /// <summary>
    ///     Detects the environment and returns resolved directories.
    ///     organization + appName are required if DotnetTool mode is detected.
    ///     fallbackMode determines what to do if no special environment is found.
    /// </summary>
    public static SimpleEnvResult Detect(
        string? organization = null,
        string? appName = null,
        SimpleAppMode fallbackMode = SimpleAppMode.ProcessPathDir)
    {
        ValidateFallbackMode(fallbackMode);

        var mode = DetermineAppMode(fallbackMode);

        // DotnetTool requires explicit org/app for per-user root resolution.
        if (mode == SimpleAppMode.DotnetTool &&
            (string.IsNullOrWhiteSpace(organization) || string.IsNullOrWhiteSpace(appName)))
            throw new InvalidOperationException(
                "DotnetTool mode detected — you must provide organization and appName.");

        var root = DetermineAppRoot(mode, organization, appName);

        return new SimpleEnvResult(
            mode,
            root,
            Path.Combine(root, "config"),
            Path.Combine(root, "log"));
    }

    /// <summary>
    ///     Creates environment info with explicit custom root directory.
    /// </summary>
    public static SimpleEnvResult GetWithCustomRoot(string customRoot)
    {
        if (string.IsNullOrWhiteSpace(customRoot))
            throw new ArgumentException("Custom root cannot be null or whitespace", nameof(customRoot));

        return new SimpleEnvResult(
            SimpleAppMode.CustomRoot,
            customRoot,
            Path.Combine(customRoot, "config"),
            Path.Combine(customRoot, "log"));
    }

    /// <summary>
    ///     STRICT: search for markers; throws if not found.
    /// </summary>
    public static SimpleEnvResult GetWithAppRootMarkers(
        string? organization = null,
        string? appName = null,
        params string[] directoryNames)
    {
        return GetWithAppRootMarkersInternal(true, organization, appName, directoryNames);
    }

    /// <summary>
    ///     SAFE: search for markers; if not found, falls back to Detect(...).
    /// </summary>
    public static SimpleEnvResult GetWithAppRootMarkersSafe(
        string? organization = null,
        string? appName = null,
        params string[] directoryNames)
    {
        return GetWithAppRootMarkersInternal(false, organization, appName, directoryNames);
    }

    // --- Replace your current GetWithAppRootMarkersInternal with this ---
    private static SimpleEnvResult GetWithAppRootMarkersInternal(
        bool throwIfNotDetected,
        string? organization,
        string? appName,
        params string[]? directoryNames)
    {
        // sanitize markers
        var markers = (directoryNames ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToArray();

        // no markers → Detect (respects DotnetTool requirements)
        if (markers.Length == 0)
            return Detect(organization, appName);

        // upward scan from BaseDirectory (case-insensitive on Windows)
        var compare = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var userRoot = FindUserRootWithMarkers(markers, compare);

        if (!string.IsNullOrEmpty(userRoot))
            return new SimpleEnvResult(
                SimpleAppMode.UserRoot,
                userRoot,
                Path.Combine(userRoot, "config"),
                Path.Combine(userRoot, "log"));

        if (throwIfNotDetected)
            throw new InvalidOperationException(
                $"No app root markers [{string.Join(", ", markers)}] found in directory hierarchy.");

        // safe fallback
        var mode = DetermineAppMode(SimpleAppMode.ProcessPathDir);
        var root = DetermineAppRoot(mode, organization, appName);
        return new SimpleEnvResult(
            mode,
            root,
            Path.Combine(root, "config"),
            Path.Combine(root, "log"));
    }

    // Exact-name upward scan: returns the first ancestor that contains ANY of the marker directories.
    // If none found, returns Directory.GetCurrentDirectory().

    // --- Replace your current FindUserRootWithMarkers + SafeEnumerateDirectories with these ---

    // Exact-name upward scan: returns the first ancestor that contains ANY of the marker directories.
    // If none found, returns null (so callers can distinguish "not found").
    private static string? FindUserRootWithMarkers(
        string[]? markers,
        StringComparison comparison = StringComparison.Ordinal,
        int? maxDepth = null)
    {
        var names = (markers ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToArray();

        if (names.Length == 0)
            return null;

        var start = AppContext.BaseDirectory;
        var current = new DirectoryInfo(start);
        var depth = 0;

        while (current != null && (maxDepth is null || depth <= maxDepth.Value))
        {
            try
            {
                foreach (var dir in SafeEnumerateDirectories(current))
                foreach (var m in names)
                    if (string.Equals(dir.Name, m, comparison))
                        return current.FullName;
            }
            catch
            {
                /* ignore and continue upward */
            }

            current = current.Parent;
            depth++;
        }

        return null;
    }

    // Avoids throwing from DirectoryInfo.GetDirectories() on access issues.
    private static DirectoryInfo[] SafeEnumerateDirectories(DirectoryInfo dir)
    {
        try
        {
            return dir.GetDirectories();
        }
        catch
        {
            return [];
        }
    }


    // ----------------------
    // Mode determination
    // ----------------------

    private static void ValidateFallbackMode(SimpleAppMode fallbackMode)
    {
        if (fallbackMode is not SimpleAppMode.CurrentUserDir and not SimpleAppMode.ProcessPathDir)
            throw new ArgumentException("Fallback mode must be CurrentUserDir or ProcessPathDir", nameof(fallbackMode));
    }

    private static SimpleAppMode DetermineAppMode(SimpleAppMode fallbackMode)
    {
        if (IsDocker()) return SimpleAppMode.Docker;
        if (IsTest()) return SimpleAppMode.Test;
        if (IsDotnetTool()) return SimpleAppMode.DotnetTool;
        if (IsDev()) return SimpleAppMode.Dev;
        return fallbackMode;
    }

    private static string DetermineAppRoot(SimpleAppMode mode, string? org, string? app)
    {
        return mode switch
        {
            // Docker default is overrideable by SIMPLE_ENV_ROOT
            SimpleAppMode.Docker => Environment.GetEnvironmentVariable("SIMPLE_ENV_ROOT") ?? "/app",

            // Your conventions:
            SimpleAppMode.Test => FindTestRoot(), // <gitRoot>/dev/app.vs  or  CWD/test-output
            SimpleAppMode.Dev => FindDevRoot(), // <gitRoot>/dev/app.vs

            // Dotnet tool: per-user root: <LOCALAPPDATA|XDG|Library>/Organization/AppName
            SimpleAppMode.DotnetTool => GetUserAppRoot(org!, app!), // validated earlier

            SimpleAppMode.CurrentUserDir => Directory.GetCurrentDirectory(),

            // IMPORTANT: AppContext.BaseDirectory is already a directory.
            // Do NOT call GetDirectoryName() on it, or you'd climb one level up.
            SimpleAppMode.ProcessPathDir => AppContext.BaseDirectory
                .TrimEnd(Path.DirectorySeparatorChar),

            _ => Directory.GetCurrentDirectory()
        };
    }

    // ----------------------
    // DotnetTool detection
    // ----------------------

    /// <summary>
    ///     Fast heuristic detection of dotnet tool (no I/O, no reflection):
    ///     - Checks ~/.dotnet/tools (global tool shims)
    ///     - Checks for ".store" in path (payloads)
    ///     - Optional env var override (SIMPLE_ENV_IS_DOTNET_TOOL=1/true)
    /// </summary>
    public static bool IsDotnetTool()
    {
        // explicit override
        var env = Environment.GetEnvironmentVariable("SIMPLE_ENV_IS_DOTNET_TOOL");
        if (!string.IsNullOrEmpty(env) && (env == "1" || env.Equals("true", StringComparison.OrdinalIgnoreCase)))
            return true;

        var baseDir = AppContext.BaseDirectory;
        if (HasDotnetToolsSegment(baseDir) || HasStoreSegment(baseDir))
            return true;

        var procPath = Environment.ProcessPath ?? string.Empty;
        if (HasDotnetToolsSegment(procPath) || HasStoreSegment(procPath))
            return true;

        return false;

        // local helpers (string-only)
        static bool HasDotnetToolsSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var norm = path.Replace('\\', '/').ToLowerInvariant();
            return norm.Contains("/.dotnet/tools/");
        }

        static bool HasStoreSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            // quick substring gate
            if (path.IndexOf(".store", StringComparison.Ordinal) < 0)
                return false;

            // segment-accurate confirmation without allocations
            var span = path.AsSpan();
            var i = 0;
            while (i < span.Length)
            {
                var rest = span.Slice(i);
                var j = rest.IndexOfAny('\\', '/');
                var seg = j >= 0 ? rest.Slice(0, j) : rest;
                if (seg.SequenceEqual(".store".AsSpan()))
                    return true;
                if (j < 0) break;
                i += j + 1;
            }

            return false;
        }
    }

    // ----------------------
    // Per-user root for DotnetTool
    // ----------------------

    private static string GetUserAppRoot(string organization, string appName)
    {
        if (string.IsNullOrWhiteSpace(organization))
            throw new ArgumentException("organization is required", nameof(organization));
        if (string.IsNullOrWhiteSpace(appName)) throw new ArgumentException("appName is required", nameof(appName));

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, organization, appName);
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            return Path.Combine(home, "Library", "Application Support", organization, appName);
        }

        // Linux/Unix: XDG_STATE_HOME or ~/.local/state
        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var baseDir = !string.IsNullOrWhiteSpace(stateHome)
            ? stateHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".local", "state");

        return Path.Combine(baseDir, organization, appName);
    }

    // ----------------------
    // Other detectors
    // ----------------------

    private static bool IsDocker()
    {
        if (File.Exists("/.dockerenv")) return true;

        var inContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        if (!string.IsNullOrEmpty(inContainer) && inContainer.Equals("true", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/1/cgroup"))
            {
                var text = File.ReadAllText("/proc/1/cgroup");
                if (text.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("kubepods", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("containerd", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            /* ignore */
        }

        return false;
    }

    private static bool IsTest()
    {
        var cmd = Environment.CommandLine;

        if (cmd.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("vstest", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("dotnet test", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("nunit", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("resharper", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VSTEST_HOST_DEBUG")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VSTEST_DATACOLLECTOR")))
            return true;

        return false;
    }

    private static bool IsDev()
    {
        return FindGitRoot() != null;
    }

    // ----------------------
    // Git root detection (supports worktrees: .git can be a file)
    // ----------------------

    private static string? FindGitRoot()
    {
        var candidates = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty),
            Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? string.Empty),
            Directory.GetCurrentDirectory()
        }.Where(p => !string.IsNullOrEmpty(p));

        foreach (var start in candidates)
        {
            var found = FindGitInPath(start!);
            if (found != null) return found;
        }

        return null;
    }

    private static string? FindGitInPath(string startPath)
    {
        var current = new DirectoryInfo(startPath);
        while (current != null)
        {
            var dotGit = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
                return current.FullName;

            current = current.Parent;
        }

        return null;
    }

    // ----------------------
    // Test/Dev roots — your conventions
    // ----------------------

    private static string FindDevRoot()
    {
        var gitRoot = FindGitRoot();
        if (gitRoot == null) return Directory.GetCurrentDirectory();
        // Convention: project root/dev/app.vs
        return Path.Combine(gitRoot, "dev", "app.vs");
    }

    private static string FindTestRoot()
    {
        var gitRoot = FindGitRoot();
        if (gitRoot != null)
            // Convention: project root/dev/app.vs even for tests
            return Path.Combine(gitRoot, "dev", "app.vs");

        // Fallback if no repo context
        return Path.Combine(Directory.GetCurrentDirectory(), "test-output");
    }
}