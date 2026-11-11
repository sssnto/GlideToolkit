using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.GlideToolkit.Core;

/// Retain NAS ownership, mode and extended ACL attributes before publishing.
/// Unsupported ownership/ACL preservation fails before touching the original.
public static class LinuxFileMetadata
{
    [DllImport("libc", SetLastError = true)] private static extern int statx(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, IntPtr result);
    [DllImport("libc", SetLastError = true)] private static extern int chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint uid, uint gid);
    [DllImport("libc", SetLastError = true)] private static extern long listxattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr list, ulong size);
    [DllImport("libc", SetLastError = true)] private static extern long getxattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr value, ulong size);
    [DllImport("libc", SetLastError = true)] private static extern int setxattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr value, ulong size, int flags);
    private static (uint Uid, uint Gid) Owner(string path)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            // Linux statx has one architecture-independent 256-byte layout.
            if (statx(-100, path, 0x100, 0x7ff, buffer) != 0) throw new RotationException("metadata_unsupported");
            return (unchecked((uint)Marshal.ReadInt32(buffer, 20)), unchecked((uint)Marshal.ReadInt32(buffer, 24)));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static byte[] Attribute(string path, string name)
    {
        var length = getxattr(path, name, IntPtr.Zero, 0);
        if (length is < 0 or > 65536) throw new RotationException("metadata_unsupported");
        var buffer = Marshal.AllocHGlobal(Math.Max(1, (int)length));
        try
        {
            if (getxattr(path, name, buffer, (ulong)length) != length) throw new RotationException("metadata_unsupported");
            var result = new byte[length]; Marshal.Copy(buffer, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public static void Preserve(string original, string staged)
    {
        if (!OperatingSystem.IsLinux()) return;
        var owner = Owner(original);
        if (Owner(staged) != owner && chown(staged, owner.Uid, owner.Gid) != 0) throw new RotationException("metadata_unsupported");
        var mode = File.GetUnixFileMode(original); File.SetUnixFileMode(staged, mode);
        var length = listxattr(original, IntPtr.Zero, 0);
        // ENOTSUP means this filesystem has no xattrs to preserve.
        if (length < 0 && Marshal.GetLastPInvokeError() == 95) return;
        if (length is < 0 or > 65536) throw new RotationException("metadata_unsupported");
        var names = Marshal.AllocHGlobal(Math.Max(1, (int)length));
        try
        {
            if (listxattr(original, names, (ulong)length) != length) throw new RotationException("metadata_unsupported");
            var bytes = new byte[length]; Marshal.Copy(names, bytes, 0, bytes.Length);
            foreach (var name in System.Text.Encoding.UTF8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var value = Attribute(original, name);
                // Inherited SELinux labels/ACLs may already be identical.
                try { if (Attribute(staged, name).SequenceEqual(value)) continue; } catch (RotationException) { }
                var buffer = Marshal.AllocHGlobal(Math.Max(1, value.Length));
                try
                {
                    Marshal.Copy(value, 0, buffer, value.Length);
                    if (setxattr(staged, name, buffer, (ulong)value.Length, 0) != 0 || !Attribute(staged, name).SequenceEqual(value)) throw new RotationException("metadata_unsupported");
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { Marshal.FreeHGlobal(names); }
        if (Owner(staged) != owner || File.GetUnixFileMode(staged) != mode) throw new RotationException("metadata_unsupported");
    }
}
