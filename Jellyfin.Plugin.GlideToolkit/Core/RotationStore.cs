using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Jellyfin.Plugin.GlideToolkit.Core;

public sealed class RotationStore
{
    private readonly string _directory;
    private readonly object _sync = new();
    public RotationStore(string directory) { _directory = directory; Directory.CreateDirectory(directory); }
    public void Save(RotationJob job)
    {
        lock (_sync)
        {
            Durability.SyncDirectory(_directory);
            if (job.State == "completed" && job.CompletedAt is null) job.CompletedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            var file = System.IO.Path.Combine(_directory, job.Id.ToString("N") + ".json");
            var temporary = file + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, job); stream.Flush(true); }
            // The journal is local configuration storage, never a media mount.
            File.Move(temporary, file, true); Durability.SyncDirectory(_directory);
        }
    }
    public List<RotationJob> Load()
    {
        lock (_sync)
        {
            var jobs = new List<RotationJob>();
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                // Fail closed if a journal is damaged. Never silently lose a pending replacement.
                try
                {
                    var job = JsonSerializer.Deserialize<RotationJob>(File.ReadAllText(file)) ?? throw new RotationException("journal_invalid");
                    // Legacy completions must retain their original order even
                    // if a deferred metadata refresh saves their journal later.
                    if (job.State == "completed" && job.CompletedAt is null) job.CompletedAt = job.UpdatedAt;
                    jobs.Add(job);
                }
                catch (JsonException) { throw new RotationException("journal_invalid"); }
            }
            return jobs;
        }
    }
}

public static partial class Durability
{
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)] private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)] private static partial int Fsync(int fd);
    [LibraryImport("libc", EntryPoint = "close")] private static partial int Close(int fd);
    public static void SyncDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        // O_RDONLY is zero on both ARM64 and x64. Avoid architecture-specific
        // O_DIRECTORY values; the path is an already validated directory.
        var fd = Open(path, 0);
        if (fd < 0) throw new RotationException("journal_open_" + Marshal.GetLastPInvokeError());
        try { if (Fsync(fd) != 0) throw new RotationException("journal_sync_" + Marshal.GetLastPInvokeError()); }
        finally { Close(fd); }
    }
}

public static class RotationFiles
{
    public static string Validate(string file, string root, bool allowMissing = false)
    {
        var full = Path.GetFullPath(file); var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(file) || !full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || full.Contains(Path.DirectorySeparatorChar + ".glide-rotation" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new RotationException("outside_library");
        var relative = Path.GetRelativePath(basePath, full); var cursor = basePath;
        CheckLink(cursor);
        var parts = relative.Split(Path.DirectorySeparatorChar);
        for (var index = 0; index < parts.Length; index++)
        {
            cursor = Path.Combine(cursor, parts[index]);
            if (allowMissing && index == parts.Length - 1 && !File.Exists(cursor) && !Directory.Exists(cursor))
            {
                // File.Exists hides dangling symlinks; LinkTarget must also be checked.
                if (new FileInfo(cursor).LinkTarget is not null) throw new RotationException("symbolic_link");
                continue;
            }
            CheckLink(cursor);
        }
        if (allowMissing && !File.Exists(full) && !Directory.Exists(full))
        {
            if (!new[] { ".mp4", ".mov", ".m4v" }.Contains(Path.GetExtension(full).ToLowerInvariant())) throw new RotationException("unsupported_format");
            return full;
        }
        if (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.Directory) != 0) throw new RotationException("file_missing");
        if (!new[] { ".mp4", ".mov", ".m4v" }.Contains(Path.GetExtension(full).ToLowerInvariant())) throw new RotationException("unsupported_format");
        return full;
    }
    public static void CheckLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new RotationException("symbolic_link");
    }
    public static string Version(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var patch = VideoMatrix.Inspect(stream, 0); var info = new FileInfo(file);
        var data = System.Text.Encoding.UTF8.GetBytes($"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{patch.Offset}:{Convert.ToHexString(patch.Original)}");
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }
    public static async Task<string> Digest(Stream stream, CancellationToken token, VideoMatrix.Patch? excluding = null)
    {
        stream.Position = 0; using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024]; long offset = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false); if (count == 0) break;
            if (excluding is null || offset + count <= excluding.Offset || offset >= excluding.Offset + 36) digest.AppendData(buffer, 0, count);
            else
            {
                var before = (int)Math.Max(0, excluding.Offset - offset);
                var after = (int)Math.Min(count, Math.Max(0, excluding.Offset + 36 - offset));
                if (before > 0) digest.AppendData(buffer, 0, before);
                if (after < count) digest.AppendData(buffer, after, count - after);
            }
            offset += count;
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }
    public static async Task<string> DigestFile(string file, CancellationToken token)
    { await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); return await Digest(stream, token).ConfigureAwait(false); }
    public static async Task Copy(Stream source, string destination, CancellationToken token)
    {
        source.Position = 0;
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
        await source.CopyToAsync(output, 1024 * 1024, token).ConfigureAwait(false); output.Flush(true);
    }
}
