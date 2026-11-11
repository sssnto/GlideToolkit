using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using Jellyfin.Plugin.GlideToolkit.Core;

namespace Jellyfin.Plugin.GlideToolkit;

public sealed class RotationJobs(RotationStore store, RotationProcessor processor, RotationLibrary library,
    ILibraryMonitor monitor, ILogger<RotationJobs> logger) : BackgroundService
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, RotationJob> _jobs = [];
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private bool _ready;
    private readonly SemaphoreSlim _recovery = new(1, 1);
    public bool Ready { get { lock (_sync) return _ready; } }
    public JobStatus Submit(ResolvedVideo video, RotationRequest request)
    {
        lock (_sync)
        {
            if (!_ready) throw new RotationException("server_preparing");
            var prior = _jobs.Values.FirstOrDefault(j => j.UserId == video.UserId && j.Request.RequestId == request.RequestId);
            if (prior is not null)
            {
                if (prior.Request != request) throw new RotationException("request_conflict");
                return prior.Status();
            }
            if (_jobs.Values.Count(j => j.State is "queued" or "running" or "committing") >= 32) throw new RotationException("queue_full");
            if (_jobs.Values.Any(j => j.Path == video.Path && j.State is "queued" or "running" or "committing" or "needs_verification")) throw new RotationException("file_busy");
            if (RotationFiles.Version(video.Path) != request.ExpectedVersion) throw new RotationException("file_changed");
            var job = new RotationJob { Id = Guid.NewGuid(), UserId = video.UserId, Request = request, Path = video.Path, Root = video.Root };
            if (request.RestoreJobId is Guid restoreId)
            {
                if (!_jobs.TryGetValue(restoreId, out var old) || old.State != "completed" || old.Path != video.Path || old.UserId != video.UserId) throw new RotationException("restore_conflict");
            }
            job.State = "accepting"; store.Save(job); _jobs.Add(job.Id, job);
            job.State = "queued";
            try { store.Save(job); }
            catch
            {
                job.State = "needs_verification"; job.Stage = "needs_verification"; job.ErrorCode = "uncertain_commit";
                try { store.Save(job); } catch { }
                throw new RotationException("uncertain_commit");
            }
            _queue.Writer.TryWrite(job.Id); return job.Status();
        }
    }
    public JobStatus? Find(Guid user, Guid id) { lock (_sync) return _jobs.TryGetValue(id, out var job) && job.UserId == user ? job.Status() : null; }
    public JobStatus? FindByRequest(Guid user, Guid request)
    { lock (_sync) return _jobs.Values.FirstOrDefault(j => j.UserId == user && j.Request.RequestId == request)?.Status(); }
    public sealed record ItemRotationState(string ItemId, string MediaSourceId, string State, string Stage, int Progress,
        string? ErrorCode, string? Revision, bool Restored, Guid? JobId, string? PreviewMediaSourceId);
    public ItemRotationState[] ItemStates(Guid user, HashSet<string> ids)
    {
        lock (_sync) return _jobs.Values.Where(j => j.UserId == user && ids.Contains(RotationLibrary.ItemKey(j.Request.ItemId)))
            .GroupBy(j => RotationLibrary.ItemKey(j.Request.ItemId), StringComparer.OrdinalIgnoreCase).Select(group =>
            {
                var latest = group.OrderByDescending(j => j.State == "completed" ? j.CompletedAt ?? j.UpdatedAt : j.UpdatedAt).First();
                var saved = group.Where(j => j.State == "completed").OrderByDescending(j => j.CompletedAt ?? j.UpdatedAt).FirstOrDefault();
                return new ItemRotationState(group.Key, latest.Request.MediaSourceId, latest.State, latest.Stage, latest.Progress,
                    latest.ErrorCode, saved?.SavedDigest, saved?.Request.RestoreJobId is not null, latest.Id, saved?.Request.MediaSourceId);
            }).ToArray();
    }
    public ResolvedVideo ResolvePreview(Guid user, string itemId, string revision)
    {
        RotationJob job;
        lock (_sync) job = _jobs.Values.Where(j => j.UserId == user && RotationLibrary.ItemKey(j.Request.ItemId) == RotationLibrary.ItemKey(itemId)
            && j.State == "completed").OrderByDescending(j => j.CompletedAt ?? j.UpdatedAt).FirstOrDefault() ?? throw new RotationException("job_missing");
        if (job.SavedDigest != revision) throw new RotationException("file_changed");
        return library.Resolve(user, job.Request.ItemId, job.Request.MediaSourceId);
    }
    public JobStatus[] RecoveryJobs()
    {
        lock (_sync) return _jobs.Values.Where(j => j.State is "needs_verification" or "recovery_failed" || j.Stage == "original_restored")
            .OrderByDescending(j => j.UpdatedAt).Take(100).Select(j => j.Status()).ToArray();
    }
    public async Task<RecoveryStatus> Recovery(Guid id, bool restoreMissing, CancellationToken token)
    {
        await _recovery.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RotationJob job;
            lock (_sync)
            {
                if (!_ready) throw new RotationException("server_preparing");
                if (!_jobs.TryGetValue(id, out job!)) throw new RotationException("job_missing");
                if (_jobs.Values.Any(j => j.Path == job.Path && j.State is "queued" or "running" or "committing")) throw new RotationException("file_busy");
            }
            library.ValidateRecovery(job);
            if (!restoreMissing) return await processor.RecoveryInfo(job, token).ConfigureAwait(false);
            monitor.ReportFileSystemChangeBeginning(job.Path);
            try { return await processor.RecoverMissing(job, token).ConfigureAwait(false); }
            finally { monitor.ReportFileSystemChangeComplete(job.Path, true); }
        }
        finally { _recovery.Release(); }
    }
    public JobStatus[] ForItem(Guid user, string itemId, string sourceId)
    {
        lock (_sync) return _jobs.Values.Where(j => j.UserId == user && j.Request.ItemId.Equals(itemId, StringComparison.OrdinalIgnoreCase)
            && j.Request.MediaSourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase)).OrderByDescending(j => j.UpdatedAt).Take(20).Select(j => j.Status()).ToArray();
    }
    public async Task<JobStatus> Verify(Guid userId, Guid id, CancellationToken token)
    {
        await _recovery.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RotationJob job;
            lock (_sync)
            {
                if (!_jobs.TryGetValue(id, out job!) || job.UserId != userId) throw new RotationException("job_missing");
                if (job.State != "needs_verification") return job.Status();
            }
            library.Revalidate(job);
            await processor.Recover(job, token).ConfigureAwait(false);
            if (job.State == "completed")
            {
                try { library.Refresh(library.Resolve(job.UserId, job.Request.ItemId, job.Request.MediaSourceId)); job.RefreshPending = false; store.Save(job); } catch { }
            }
            return job.Status();
        }
        finally { _recovery.Release(); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var loaded = store.Load();
            foreach (var job in loaded)
            {
                if (job.State != "queued") await processor.Recover(job, stoppingToken).ConfigureAwait(false);
                if (job.State == "completed" && job.RefreshPending)
                {
                    try { library.Refresh(library.Resolve(job.UserId, job.Request.ItemId, job.Request.MediaSourceId)); job.RefreshPending = false; store.Save(job); } catch { }
                }
                lock (_sync) _jobs.Add(job.Id, job);
            }
            lock (_sync) _ready = true;
            foreach (var job in loaded.Where(j => j.State == "queued")) _queue.Writer.TryWrite(job.Id);
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                RotationJob job; RotationJob? restore;
                lock (_sync) { job = _jobs[id]; restore = job.Request.RestoreJobId is Guid prior ? _jobs.GetValueOrDefault(prior) : null; }
                var monitored = false;
                try
                {
                    library.Revalidate(job);
                    var video = library.Resolve(job.UserId, job.Request.ItemId, job.Request.MediaSourceId);
                    for (var attempt = 0; library.HasPlayback(video); attempt++)
                    {
                        if (attempt >= 30) throw new RotationException("active_playback");
                        job.Stage = "waiting_playback"; store.Save(job);
                        await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
                    }
                    monitor.ReportFileSystemChangeBeginning(job.Path); monitored = true;
                    await processor.Execute(job, restore, stoppingToken, () =>
                    {
                        library.Revalidate(job);
                        if (library.HasPlayback(video)) throw new RotationException("active_playback");
                    }).ConfigureAwait(false);
                    try { library.Refresh(video); job.RefreshPending = false; store.Save(job); } catch { job.RefreshPending = true; store.Save(job); }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    processor.Fail(job, error);
                    // Never log exception messages: IO exceptions can contain private media paths.
                    logger.LogWarning("Glide rotation job {JobId} ended with {Code}; detail {FailureCode}", job.Id, job.ErrorCode, job.FailureCode);
                }
                finally { if (monitored) monitor.ReportFileSystemChangeComplete(job.Path, false); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch { logger.LogError("Glide rotation journal could not be loaded; writes disabled"); }
        finally { lock (_sync) _ready = false; }
    }
}
