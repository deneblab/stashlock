using System;
using System.IO;
using System.Text;

namespace Deneblab.StashLock.Cli.Common.Security;

/// <summary>
/// Writes files that hold secrets (private keys, decrypted output) readable only by the owner.
/// On Unix the file is created with mode 0600; on Windows the profile ACLs apply.
/// </summary>
internal static class SecureFile
{
    public static void WriteAllText(string path, string contents)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, contents);
            return;
        }

        // UnixCreateMode only applies to new files, so replace any existing one
        if (File.Exists(path))
            File.Delete(path);

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        };
        using var stream = new FileStream(path, options);
        var bytes = new UTF8Encoding(false).GetBytes(contents);
        stream.Write(bytes, 0, bytes.Length);
    }
}
