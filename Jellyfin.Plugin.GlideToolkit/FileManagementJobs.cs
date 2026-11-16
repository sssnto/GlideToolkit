using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Jellyfin.Plugin.GlideToolkit.Core;

namespace Jellyfin.Plugin.GlideToolkit;

public sealed class FileManagementJobs(FileActionStore store, FileManagementLibrary library, RotationJobs rotations, MediaMutationGate gate) : BackgroundService
{
    private readonly object _sync = new();
    private readonly List<FileActionBatch> _batches = [];
    private readonly FileActionProcessor _processor = new(store);
    public bool Ready { get; private set; }
    private readonly Dictionary<Guid, DateTime> _refreshAttempts = [];
    public object Inspect(Guid user, string[] items)
    {
        library.Require(user);
        if (items is null || items.Length is < 1 or > 500) throw new RotationException("invalid_batch");
        var directories = new Dictionary<string, string[]>(StringComparer.Ordinal);
        return items.Distinct(StringComparer.OrdinalIgnoreCase).Select(id => {
            try {
                var video = library.Resolve(user, id); var files = ManagedMediaFiles.Bundle(video.Path, directories);
                foreach (var path in files) ManagedMediaFiles.Validate(video.Root, path);
                return new { itemId = id, mediaSourceId = video.SourceId, title = Path.GetFileName(video.Path), version = ManagedMediaFiles.Version(video.Path), relatedFiles = files.Count - 1, errorCode = (string?)null };
            } catch (RotationException e) { return new { itemId = id, mediaSourceId = "", title = "", version = "", relatedFiles = 0, errorCode = (string?)e.Code }; }
              catch (IOException) { return new { itemId = id, mediaSourceId = "", title = "", version = "", relatedFiles = 0, errorCode = (string?)"file_unavailable" }; }
        }).ToArray();
    }
    public object Submit(Guid user, FileActionRequest request)
    {
        library.Require(user);
        lock (_sync)
        {
            if (!Ready) throw new RotationException("server_preparing");
            var prior = _batches.FirstOrDefault(b => b.UserId == user && b.Request.RequestId == request.RequestId);
            if (prior is not null) {
                if (JsonSerializer.Serialize(prior.Request) != JsonSerializer.Serialize(request)) throw new RotationException("request_conflict");
                return prior.Status();
            }
            if (request.RequestId == Guid.Empty || request.Items is null || request.Items.Length is < 1 or > 500 || !new[] { "move", "trash", "restore" }.Contains(request.Operation)) throw new RotationException("invalid_batch");
            if (request.Items.Select(i => i.ItemId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Items.Length) throw new RotationException("invalid_batch");
            if (_batches.Count(b => b.State is "queued" or "running" or "paused") >= 8) throw new RotationException("queue_full");
            var batch = new FileActionBatch { UserId = user, Request = request };
            var directories = new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (request.Operation == "restore") PrepareRestore(user, request, batch);
            else {
                var destination = request.Operation == "move" ? library.Folder(user, request.DestinationId ?? "", true) : null;
                batch.DestinationId = destination?.Id ?? ""; batch.DestinationName = destination?.Name ?? "服务器回收目录";
                foreach (var item in request.Items) {
                    var video = library.Resolve(user, item.ItemId, item.MediaSourceId);
                    if (ManagedMediaFiles.Version(video.Path) != item.ExpectedVersion) throw new RotationException("file_changed");
                    if (destination is not null && destination.Root != video.Root) throw new RotationException("same_library_required");
                    var target = destination?.Path ?? Path.Combine(Path.GetDirectoryName(video.Path)!, ".glide-trash", batch.Id.ToString("N"), Guid.NewGuid().ToString("N"));
                    var entry = new FileActionEntry { ItemId = item.ItemId, MediaSourceId = video.SourceId, Title = Path.GetFileName(video.Path), Root = video.Root };
                    foreach (var path in ManagedMediaFiles.Bundle(video.Path, directories)) {
                        ManagedMediaFiles.Validate(video.Root, path);
                        entry.Files.Add(new FilePair { Source = path, Target = Path.Combine(target!, Path.GetFileName(path)), Version = ManagedMediaFiles.Version(path) });
                    }
                    if (entry.Files.Any(p => p.Source == p.Target)) throw new RotationException("same_directory");
                    batch.Entries.Add(entry);
                }
            }
            var paths = batch.Entries.SelectMany(e => e.Files.Select(p => p.Source)).ToHashSet(StringComparer.Ordinal);
            if (paths.Count != batch.Entries.Sum(e => e.Files.Count)) throw new RotationException("shared_sidecar");
            if (_batches.Any(b => b.Entries.Any(e => e.State is "queued" or "running" or "needs_verification" && e.Files.Any(p => paths.Contains(p.Source) || paths.Contains(p.Target))))) throw new RotationException("file_busy");
            store.Save(batch); _batches.Add(batch);
            if (request.RestoreBatchId is Guid original) { var restored = _batches.Single(b => b.Id == original && b.UserId == user); restored.RestoredBy = batch.Id; store.Save(restored); }
            return batch.Status();
        }
    }
    private void PrepareRestore(Guid user, FileActionRequest request, FileActionBatch batch)
    {
        var old = _batches.SingleOrDefault(b => b.UserId == user && b.Id == request.RestoreBatchId) ?? throw new RotationException("job_missing");
        if (old.RestoredBy is not null || old.Request.Operation == "restore" || old.Entries.Any(e => e.State is "queued" or "running")) throw new RotationException("restore_conflict");
        batch.DestinationName = "原目录";
        var ids = request.Items.Select(i => i.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in old.Entries.Where(e => ids.Contains(e.ItemId) && e.State == "completed")) {
            batch.Entries.Add(new FileActionEntry { ItemId = entry.ItemId, MediaSourceId = entry.MediaSourceId, Title = entry.Title, Root = entry.Root,
                Files = entry.Files.Select(p => new FilePair { Source = p.Target, Target = p.Source, Version = p.Version }).ToList() });
        }
        if (batch.Entries.Count == 0 || batch.Entries.Count != ids.Count) throw new RotationException("restore_conflict");
    }
    public object[] List(Guid user) { library.Require(user); lock (_sync) return _batches.Where(b => b.UserId == user).OrderByDescending(b => b.UpdatedAt).Take(100).Select(b => b.Status(includeItems: false)).ToArray(); }
    public object Get(Guid user, Guid id) { library.Require(user); lock (_sync) return Find(user, id).Status(); }
    private FileActionBatch Find(Guid user, Guid id) => _batches.SingleOrDefault(b => b.Id == id && b.UserId == user) ?? throw new RotationException("job_missing");
    public object Change(Guid user, Guid id, string command)
    {
        library.Require(user); lock (_sync) {
            var batch = Find(user, id);
            batch.Change(command);
            store.Save(batch); return batch.Status();
        }
    }
    private async Task Refresh(FileActionBatch batch, CancellationToken token)
    {
        _refreshAttempts[batch.Id] = DateTime.UtcNow;
        try { await library.Refresh(batch.Entries.Where(e => e.State == "completed"), token).ConfigureAwait(false); batch.RefreshPending = false; store.Save(batch); }
        catch { batch.RefreshPending = true; try { store.Save(batch); } catch { } }
    }
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        try {
            var loaded = store.Load();
            foreach (var batch in loaded) {
                var interrupted = batch.Entries.Any(e => e.State == "running");
                foreach (var entry in batch.Entries) _processor.Recover(batch, entry);
                if (interrupted) { batch.RefreshPending = true; if (batch.Entries.All(e => e.State != "queued")) batch.State = batch.Entries.All(e => e.State == "completed") ? "completed" : "finished"; store.Save(batch); }
                if (batch.Entries.Any(e => e.State == "needs_verification")) batch.State = "needs_verification";
                _batches.Add(batch);
            }
            foreach (var restoration in loaded.Where(b => b.Request.RestoreBatchId is not null)) {
                var prior = loaded.SingleOrDefault(b => b.Id == restoration.Request.RestoreBatchId && b.UserId == restoration.UserId);
                if (prior is not null) { prior.RestoredBy = restoration.Id; store.Save(prior); }
            }
            Ready = true;
            while (!token.IsCancellationRequested) {
                FileActionBatch? batch; FileActionEntry? entry;
                lock (_sync) { batch = _batches.FirstOrDefault(b => b.State is "queued" or "running" && b.Entries.Any(e => e.State == "queued")); entry = batch?.Entries.First(e => e.State == "queued"); }
                if (batch is null || entry is null || !rotations.Ready) {
                    FileActionBatch? refresh;
                    lock (_sync) refresh = _batches.FirstOrDefault(b => b.RefreshPending && b.State is not ("queued" or "running") && (!_refreshAttempts.TryGetValue(b.Id, out var attempted) || (DateTime.UtcNow - attempted).TotalSeconds >= 30));
                    if (refresh is not null) await Refresh(refresh, token).ConfigureAwait(false);
                    await Task.Delay(400, token).ConfigureAwait(false); continue;
                }
                await gate.Semaphore.WaitAsync(token).ConfigureAwait(false);
                try {
                    lock (_sync) { if (batch.State is "paused" or "cancelled") continue; batch.State = "running"; }
                    library.ValidateRecorded(batch.UserId, entry, string.IsNullOrEmpty(batch.DestinationId) ? null : batch.DestinationId, batch.Request.Operation == "restore");
                    if (entry.Files.Any(p => rotations.BusyPath(p.Source) || rotations.BusyPath(p.Target))) throw new RotationException("file_busy");
                    if (batch.Request.Operation == "trash") {
                        var destination = Path.GetDirectoryName(entry.Files[0].Target)!;
                        ManagedMediaFiles.MakeRecycle(entry.Root, Path.GetDirectoryName(Path.GetDirectoryName(destination)!)!);
                        ManagedMediaFiles.MakeRecycle(entry.Root, Path.GetDirectoryName(destination)!);
                        ManagedMediaFiles.MakeRecycle(entry.Root, destination);
                    }
                    _processor.Execute(batch, entry, () => library.ValidateRecorded(batch.UserId, entry,
                        string.IsNullOrEmpty(batch.DestinationId) ? null : batch.DestinationId, batch.Request.Operation == "restore", verifySource: false));
                    batch.RefreshPending = true;
                }
                catch (Exception error) {
                    if (entry.State != "needs_verification") { entry.State = "failed"; entry.ErrorCode = error is RotationException re ? re.Code : error is UnauthorizedAccessException ? "write_denied" : "file_unavailable"; }
                }
                finally {
                    lock (_sync) {
                        if (entry.State == "needs_verification") batch.State = "needs_verification";
                        else if (batch.State is not ("paused" or "cancelled")) batch.State = batch.Entries.Any(e => e.State == "queued") ? "queued" : batch.Entries.All(e => e.State == "completed") ? "completed" : "finished";
                        try { store.Save(batch); } catch { batch.State = "needs_verification"; }
                    }
                    gate.Semaphore.Release();
                }
                if (batch.RefreshPending && batch.State != "queued") {
                    await Refresh(batch, token).ConfigureAwait(false);
                }
            }
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { }
          catch { Ready = false; }
        finally { Ready = false; }
    }
}
