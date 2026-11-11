using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.GlideToolkit.Core;

public enum PublicationMode { Exchange, SameDirectoryRename }
public interface IAtomicPublication
{
    PublicationMode Probe(string parent, string work);
    void Exchange(string staged, string original);
    void RenameSameDirectory(string staged, string original);
    void PublishMissing(string staged, string destination);
}

/// No unlink/copy fallback. An unsupported NAS must refuse before modifying media.
public sealed class AtomicPublication : IAtomicPublication
{
    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int RenameNative([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        int newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);
    [DllImport("libc", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameMac([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);

    private static void Rename(string source, string destination, bool exchange)
    {
        int result;
        try
        {
            result = OperatingSystem.IsLinux() ? RenameAt2(-100, source, -100, destination, exchange ? 2u : 1u)
                : OperatingSystem.IsMacOS() ? RenameMac(source, destination, exchange ? 2u : 4u)
                : throw new RotationException("atomic_replace_unsupported");
        }
        catch (EntryPointNotFoundException) { throw new RotationException("atomic_replace_unsupported"); }
        if (result != 0) throw new RotationException("atomic_" + (exchange ? "exchange_" : "publish_") + Marshal.GetLastPInvokeError());
    }
    public void Exchange(string staged, string original) => Rename(staged, original, true);
    public void PublishMissing(string staged, string destination) => Rename(staged, destination, false);

    public void RenameSameDirectory(string staged, string original)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new RotationException("atomic_replace_unsupported");
        if (Path.GetDirectoryName(Path.GetFullPath(staged)) != Path.GetDirectoryName(Path.GetFullPath(original))) throw new RotationException("atomic_cross_directory_refused");
        // Native POSIX rename only. In particular, EXDEV must never fall back to
        // copying/truncating the destination as managed File.Move can do.
        if (RenameNative(staged, original) != 0) throw new RotationException("atomic_rename_" + Marshal.GetLastPInvokeError());
    }
    public PublicationMode Probe(string parent, string work)
    {
        try { ProbeExchange(parent, work); return PublicationMode.Exchange; }
        catch (RotationException error) when (error.Code.StartsWith("atomic_", StringComparison.Ordinal))
        {
            ProbeSameDirectory(parent);
            return PublicationMode.SameDirectoryRename;
        }
    }
    public void ProbeSameDirectory(string parent)
    {
        var name = ".glide-rename-probe-" + Guid.NewGuid().ToString("N");
        var source = Path.Combine(parent, name + "-a"); var destination = Path.Combine(parent, name + "-b");
        var owned = new HashSet<string>(StringComparer.Ordinal);
        var a = Guid.NewGuid().ToByteArray(); var b = Guid.NewGuid().ToByteArray();
        try
        {
            Write(source, a); owned.Add(source); Write(destination, b); owned.Add(destination);
            RenameSameDirectory(source, destination);
            if (File.Exists(source) || !File.ReadAllBytes(destination).SequenceEqual(a)) throw new RotationException("atomic_probe_failed");
            Durability.SyncDirectory(parent);
        }
        catch (IOException error) { throw new RotationException("atomic_probe_same_directory_io_" + (error.HResult & 0xffff)); }
        finally { foreach (var file in owned) { try { File.Delete(file); } catch { } } }
    }
    private void ProbeExchange(string parent, string work)
    {
        // Exercise the actual cross-directory filesystem operations using only
        // random, exclusively-created files; never probe with the user's video.
        var name = ".glide-atomic-probe-" + Guid.NewGuid().ToString("N");
        var first = Path.Combine(parent, name); var second = Path.Combine(work, name);
        var published = Path.Combine(parent, name + "-published");
        var a = Guid.NewGuid().ToByteArray(); var b = Guid.NewGuid().ToByteArray();
        var owned = new HashSet<string>(StringComparer.Ordinal);
        var step = "create";
        try
        {
            Write(first, a); owned.Add(first); Write(second, b); owned.Add(second);
            step = "exchange";
            Exchange(second, first);
            step = "read_exchange";
            if (!File.ReadAllBytes(first).SequenceEqual(b) || !File.ReadAllBytes(second).SequenceEqual(a)) throw new RotationException("atomic_probe_failed");
            // A no-replace operation MUST reject an existing destination.
            var refused = false;
            step = "refuse_existing";
            try { PublishMissing(second, first); } catch (RotationException) { refused = true; }
            if (!refused || !File.ReadAllBytes(first).SequenceEqual(b) || !File.ReadAllBytes(second).SequenceEqual(a)) throw new RotationException("atomic_probe_failed");
            step = "publish_missing";
            PublishMissing(second, published);
            owned.Add(published);
            step = "read_published";
            if (File.Exists(second) || !File.ReadAllBytes(published).SequenceEqual(a)) throw new RotationException("atomic_probe_failed");
            Durability.SyncDirectory(parent); Durability.SyncDirectory(work);
        }
        catch (IOException error) { throw new RotationException("atomic_probe_" + step + "_io_" + (error.HResult & 0xffff)); }
        finally
        {
            // Only the three exact random paths owned by this invocation.
            foreach (var file in owned) { try { File.Delete(file); } catch { } }
        }
    }
    private static void Write(string path, byte[] data)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(data); file.Flush(true);
    }
}
