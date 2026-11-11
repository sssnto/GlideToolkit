using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.GlideToolkit.Core;

/// SMB/CIFS can turn flock into a mandatory remote lock. Verify acquisition,
/// then release and close this descriptor BEFORE any rename operation.
public static class PublicationFileAccess
{
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int descriptor, int operation);

    public static void CheckAndRelease(string file)
    {
        try
        {
            // For writable SMB/NFS descriptors .NET deliberately skips its
            // automatic shared flock. Take exactly one explicit exclusive
            // lock; repeating an automatic exclusive lock can itself return
            // EACCES over SMB even for the same descriptor.
            using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new RotationException("platform_unsupported");
            var descriptor = handle.DangerousGetHandle().ToInt32();
            // .NET ignores some flock errors as advisory/best-effort. On SMB
            // that is insufficient: require success, never publish on failure.
            if (Flock(descriptor, 2 | 4) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new RotationException(error is 11 or 35 ? "file_busy" : "file_lock_" + error);
            }
            if (Flock(descriptor, 8) != 0) throw new RotationException("file_unlock_" + Marshal.GetLastPInvokeError());
        }
        catch (UnauthorizedAccessException) { throw new RotationException("write_denied"); }
        catch (IOException) { throw new RotationException("file_busy"); }
    }
}
