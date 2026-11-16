using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.GlideToolkit.Core;

public sealed class MediaMutationGate { public SemaphoreSlim Semaphore { get; } = new(1, 1); }
public sealed record FileActionItem(string ItemId, string MediaSourceId, string ExpectedVersion, string Title);
public sealed record FileActionRequest(Guid RequestId, string Operation, FileActionItem[] Items, string? DestinationId = null, Guid? RestoreBatchId = null);
public sealed class FilePair
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Version { get; set; } = "";
    public string State { get; set; } = "pending";
}
public sealed class FileActionEntry
{
    public string ItemId { get; set; } = "";
    public string MediaSourceId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Root { get; set; } = "";
    public string State { get; set; } = "queued";
    public string? ErrorCode { get; set; }
    public List<FilePair> Files { get; set; } = [];
}
public sealed class FileActionBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public FileActionRequest Request { get; set; } = new(Guid.Empty, "", []);
    public string DestinationName { get; set; } = "";
    public string DestinationId { get; set; } = "";
    public string State { get; set; } = "queued";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<FileActionEntry> Entries { get; set; } = [];
    public Guid? RestoredBy { get; set; }
    public bool RefreshPending { get; set; }
    public void Change(string command)
    {
        if (command == "pause" && State is "queued" or "running") State = "paused";
        else if (command == "resume" && State == "paused" && Entries.All(e => e.State != "needs_verification")) State = "queued";
        else if (command == "cancel" && State is "queued" or "running" or "paused") { foreach (var entry in Entries.Where(e => e.State == "queued")) entry.State = "cancelled"; State = "cancelled"; }
        else if (command == "retry" && Entries.Any(e => e.State == "failed") && Entries.All(e => e.State is not ("running" or "needs_verification"))) {
            foreach (var entry in Entries.Where(e => e.State == "failed")) { entry.State = "queued"; entry.ErrorCode = null; }
            State = "queued";
        } else throw new RotationException("invalid_action");
    }
    public object Status(bool includeItems = true) => new {
        batchId = Id, requestId = Request.RequestId, operation = Request.Operation, destinationName = DestinationName,
        state = State, updatedAt = UpdatedAt, total = Entries.Count,
        completed = Entries.Count(e => e.State == "completed"), failed = Entries.Count(e => e.State == "failed"),
        remaining = Entries.Count(e => e.State is "queued" or "running"), cancelled = Entries.Count(e => e.State == "cancelled"),
        needsVerification = Entries.Count(e => e.State == "needs_verification"), canRestore = RestoredBy is null && Request.Operation != "restore" && Entries.Any(e => e.State == "completed") && Entries.All(e => e.State is not ("queued" or "running")),
        refreshPending = RefreshPending,
        items = Entries.Where(_ => includeItems).Select(e => new { itemId = e.ItemId, mediaSourceId = e.MediaSourceId, title = e.Title, state = e.State, errorCode = e.ErrorCode, relatedFiles = e.Files.Count - 1 }).ToArray()
    };
}
public sealed class FileActionStore(string directory)
{
    private readonly object _sync = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    public void Save(FileActionBatch batch)
    {
        lock (_sync) SaveLocked(batch);
    }
    private void SaveLocked(FileActionBatch batch)
    {
        Directory.CreateDirectory(directory); batch.UpdatedAt = DateTime.UtcNow;
        var file = Path.Combine(directory, batch.Id.ToString("N") + ".json");
        using (var stream = new FileStream(file + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, batch, Json); stream.Flush(true); }
        File.Move(file + ".tmp", file, true); Durability.SyncDirectory(directory);
    }
    public List<FileActionBatch> Load()
    {
        Directory.CreateDirectory(directory);
        return Directory.EnumerateFiles(directory, "*.json").Select(file => JsonSerializer.Deserialize<FileActionBatch>(File.ReadAllText(file), Json) ?? throw new RotationException("journal_invalid")).ToList();
    }
}
public static class ManagedMediaFiles
{
    private static readonly HashSet<string> Formats = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".ts", ".m2ts", ".webm" };
    private static readonly HashSet<string> Sidecars = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".ass", ".ssa", ".vtt", ".sup", ".sub", ".idx" };
    public static bool IsVideo(string file) => Formats.Contains(Path.GetExtension(file));
    public static bool Within(string root, string file, bool equal = false)
    {
        var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar); var full = Path.GetFullPath(file);
        return (equal && full == basePath) || full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    public static void Validate(string root, string file, bool directory = false, bool missing = false, bool recycle = false)
    {
        if (!Path.IsPathFullyQualified(file) || !Within(root, file, equal: directory)) throw new RotationException("outside_library");
        var cursor = Path.GetFullPath(root); Check(cursor);
        var relative = Path.GetRelativePath(cursor, Path.GetFullPath(file));
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            if (part == ".") continue;
            if (part.StartsWith(".glide-", StringComparison.Ordinal) && !(recycle && part == ".glide-trash")) throw new RotationException("reserved_path");
            cursor = Path.Combine(cursor, part);
            var info = new FileInfo(cursor);
            if (info.LinkTarget is not null) throw new RotationException("symbolic_link");
            if (!Directory.Exists(cursor) && !File.Exists(cursor))
            {
                if (missing && cursor == Path.GetFullPath(file)) return;
                throw new RotationException("file_missing");
            }
            Check(cursor);
        }
        if (directory ? !Directory.Exists(file) : !File.Exists(file)) throw new RotationException("file_missing");
    }
    private static void Check(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).LinkTarget is not null) throw new RotationException("symbolic_link");
    }
    public static string Version(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var info = new FileInfo(file); using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(Encoding.UTF8.GetBytes(info.Length + ":" + info.LastWriteTimeUtc.Ticks + ":"));
        var buffer = new byte[65536]; var read = stream.Read(buffer); digest.AppendData(buffer.AsSpan(0, read));
        if (stream.Length > buffer.Length) { stream.Position = Math.Max(buffer.Length, stream.Length - buffer.Length); read = stream.Read(buffer); digest.AppendData(buffer.AsSpan(0, read)); }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }
    public static List<string> Bundle(string file, Dictionary<string, string[]>? directories = null)
    {
        var parent = Path.GetDirectoryName(file)!; var stem = Path.GetFileNameWithoutExtension(file);
        var result = new List<string> { file };
        if (directories is null || !directories.TryGetValue(parent, out var candidates))
        {
            candidates = Directory.EnumerateFiles(parent).Take(10001).ToArray();
            if (candidates.Length > 10000) throw new RotationException("directory_too_large");
            directories?.Add(parent, candidates);
        }
        var videoStems = candidates.Where(IsVideo).Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var name = Path.GetFileNameWithoutExtension(candidate);
            if (!Sidecars.Contains(Path.GetExtension(candidate)) || !(name == stem || name.StartsWith(stem + ".", StringComparison.Ordinal))) continue;
            // A dotted name can be another video, rather than a language suffix.
            // Prefer that video's exact subtitle over the shorter filename prefix.
            var owner = name;
            while (owner.Length > stem.Length && !videoStems.Contains(owner)) {
                var separator = owner.LastIndexOf('.');
                if (separator < 0) break;
                owner = owner[..separator];
            }
            if (owner.Length <= stem.Length || !videoStems.Contains(owner)) result.Add(candidate);
            if (result.Count > 33) throw new RotationException("too_many_sidecars");
        }
        return result;
    }
    public static void MakeRecycle(string root, string directory)
    {
        var parent = Path.GetDirectoryName(directory)!;
        Validate(root, parent, directory: true, recycle: true);
        if (new DirectoryInfo(directory).LinkTarget is not null || File.Exists(directory)) throw new RotationException("symbolic_link");
        Directory.CreateDirectory(directory);
        Validate(root, directory, directory: true, recycle: true);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

/// Atomic, no-overwrite rename only. No implicit copy/delete fallback for large or cross-volume files.
public sealed class FileActionProcessor(FileActionStore store, IAtomicPublication? publication = null)
{
    private readonly IAtomicPublication _native = publication ?? new AtomicPublication();
    public void Execute(FileActionBatch batch, FileActionEntry entry, Action revalidate)
    {
        revalidate();
        foreach (var pair in entry.Files)
        {
            ManagedMediaFiles.Validate(entry.Root, pair.Source, recycle: true);
            ManagedMediaFiles.Validate(entry.Root, pair.Target, missing: true, recycle: true);
            if (File.Exists(pair.Target) || Directory.Exists(pair.Target)) throw new RotationException("destination_exists");
            if (ManagedMediaFiles.Version(pair.Source) != pair.Version) throw new RotationException("file_changed");
            // Close this probe before rename: SMB/CIFS may enforce sharing locks.
            using var probe = new FileStream(pair.Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        entry.State = "running"; store.Save(batch);
        try
        {
            foreach (var pair in entry.Files)
            {
                revalidate();
                ManagedMediaFiles.Validate(entry.Root, pair.Source, recycle: true);
                ManagedMediaFiles.Validate(entry.Root, pair.Target, missing: true, recycle: true);
                if (ManagedMediaFiles.Version(pair.Source) != pair.Version) throw new RotationException("file_changed");
                pair.State = "moving"; store.Save(batch);
                _native.PublishMissing(pair.Source, pair.Target);
                pair.State = "moved"; store.Save(batch);
                if (ManagedMediaFiles.Version(pair.Target) != pair.Version) throw new RotationException("file_changed");
            }
            Sync(entry); entry.State = "completed"; entry.ErrorCode = null; store.Save(batch);
        }
        catch
        {
            RollBack(batch, entry);
            throw;
        }
    }
    public void Recover(FileActionBatch batch, FileActionEntry entry)
    {
        if (entry.State != "running") return;
        var allMoved = entry.Files.All(p => !File.Exists(p.Source) && File.Exists(p.Target) && ManagedMediaFiles.Version(p.Target) == p.Version);
        if (allMoved) { Sync(entry); entry.State = "completed"; store.Save(batch); return; }
        RollBack(batch, entry);
        if (entry.State != "needs_verification") { entry.State = "failed"; entry.ErrorCode = "interrupted"; store.Save(batch); }
    }
    private void RollBack(FileActionBatch batch, FileActionEntry entry)
    {
        try
        {
            foreach (var pair in entry.Files.AsEnumerable().Reverse())
            {
                ManagedMediaFiles.Validate(entry.Root, pair.Source, missing: true, recycle: true);
                ManagedMediaFiles.Validate(entry.Root, pair.Target, missing: true, recycle: true);
                var source = File.Exists(pair.Source); var target = File.Exists(pair.Target);
                if (source && ManagedMediaFiles.Version(pair.Source) == pair.Version && pair.State != "moved") { pair.State = "pending"; continue; }
                if (source && !target && ManagedMediaFiles.Version(pair.Source) == pair.Version) { pair.State = "pending"; continue; }
                if (!source && target && ManagedMediaFiles.Version(pair.Target) == pair.Version) { _native.PublishMissing(pair.Target, pair.Source); pair.State = "pending"; store.Save(batch); continue; }
                throw new RotationException("uncertain_commit");
            }
            Sync(entry); entry.State = "failed"; store.Save(batch);
        }
        catch { entry.State = "needs_verification"; entry.ErrorCode = "uncertain_commit"; try { store.Save(batch); } catch { } }
    }
    private static void Sync(FileActionEntry entry)
    {
        foreach (var path in entry.Files.SelectMany(p => new[] { Path.GetDirectoryName(p.Source)!, Path.GetDirectoryName(p.Target)! }).Distinct()) Durability.SyncDirectory(path);
    }
}
