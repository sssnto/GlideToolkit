using System.Text.Json;

namespace Jellyfin.Plugin.GlideToolkit.Core;

public sealed record BatchItemRequest(string ItemId, string? MediaSourceId, string Title);
public sealed record BatchRequest(Guid RequestId, int QuarterTurns, BatchItemRequest[] Items);
public sealed record BatchPrepared(string ItemId, string MediaSourceId, string Version, string Title);
public sealed class BatchEntry
{
    public string ItemId { get; set; } = "";
    public string? MediaSourceId { get; set; }
    public string Title { get; set; } = "";
    public string? Version { get; set; }
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public Guid? JobId { get; set; }
    public string State { get; set; } = "queued";
    public string? ErrorCode { get; set; }
}
public sealed class RotationBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public BatchRequest Request { get; set; } = null!;
    public List<BatchEntry> Entries { get; set; } = [];
    public bool Paused { get; set; }
    public bool Cancelled { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
public sealed record BatchEntryStatus(string ItemId, string? MediaSourceId, string Title, string State,
    Guid? JobId, string? ErrorCode, string? Stage, int Progress);
public sealed record BatchStatus(Guid BatchId, Guid RequestId, int QuarterTurns, string State,
    int Total, int Completed, int Failed, int Remaining, int Cancelled, int NeedsVerification,
    DateTime UpdatedAt, BatchEntryStatus? Current, BatchEntryStatus[]? Items);

/// Durable dispatch ledger. Only one child is fed to the existing serial media worker.
/// Child request identities are flushed BEFORE submission; replay consults the worker
/// journal first, including when a submission response or the server process is lost.
public sealed class RotationBatches(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private readonly List<RotationBatch> _batches = [];
    private bool _ready;
    public bool Ready { get { lock (_sync) return _ready; } }
    public void Load()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                _batches.Add(JsonSerializer.Deserialize<RotationBatch>(File.ReadAllBytes(file), Json) ?? throw new IOException("batch_journal_invalid"));
            _ready = true;
        }
    }
    private void Save(RotationBatch batch)
    {
        batch.UpdatedAt = DateTime.UtcNow;
        var path = Path.Combine(directory, batch.Id.ToString("N") + ".json");
        var temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, batch, Json); stream.Flush(true); }
            File.Move(temp, path, true); Durability.SyncDirectory(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A failed flush/rename may already have committed the ledger. Stop
            // all new dispatch until restart reloads it; never accept a second
            // batch with new child identities while the first is uncertain.
            _ready = false; throw;
        }
    }
    public BatchStatus Submit(Guid user, BatchRequest request)
    {
        lock (_sync)
        {
            RequireReady();
            if (request.RequestId == Guid.Empty || request.QuarterTurns is < 1 or > 3 || request.Items is null || request.Items.Length is < 1 or > 1000
                || request.Items.Any(i => i is null || !Guid.TryParse(i.ItemId, out _) || i.Title is null || i.Title.Length > 512 || i.MediaSourceId?.Length > 128)
                || request.Items.Select(i => Guid.Parse(i.ItemId)).Distinct().Count() != request.Items.Length) throw new RotationException("invalid_batch");
            var prior = _batches.FirstOrDefault(b => b.UserId == user && b.Request.RequestId == request.RequestId);
            if (prior is not null)
            {
                if (JsonSerializer.Serialize(prior.Request, Json) != JsonSerializer.Serialize(request, Json)) throw new RotationException("request_conflict");
                return Status(prior, false, null);
            }
            var wanted = request.Items.Select(i => Guid.Parse(i.ItemId)).ToHashSet();
            if (_batches.Any(b => b.UserId == user && b.Entries.Any(e => wanted.Contains(Guid.Parse(e.ItemId)) && e.State is "queued" or "running" or "needs_verification"))) throw new RotationException("file_busy");
            if (_batches.Count(b => b.UserId == user && b.Entries.Any(e => e.State is "queued" or "running")) >= 8) throw new RotationException("queue_full");
            var batch = new RotationBatch { UserId = user, Request = request, Entries = request.Items.Select(i => new BatchEntry { ItemId = Guid.Parse(i.ItemId).ToString("N"), MediaSourceId = i.MediaSourceId, Title = ShortTitle(i.Title) }).ToList() };
            Save(batch); _batches.Add(batch); return Status(batch, false, null);
        }
    }
    public BatchStatus[] List(Guid user, Func<Guid, Guid, JobStatus?> lookup)
    { lock (_sync) { RequireReady(); return _batches.Where(b => b.UserId == user).OrderByDescending(b => b.CreatedAt).Take(100).Select(b => Status(b, false, lookup)).ToArray(); } }
    public BatchStatus Get(Guid user, Guid id, Func<Guid, Guid, JobStatus?> lookup, bool includeItems = true)
    { lock (_sync) return Status(Find(user, id), includeItems, lookup); }
    public BatchStatus Change(Guid user, Guid id, string action, Func<Guid, Guid, JobStatus?> lookup, Func<Guid, Guid, JobStatus?> byRequest)
    {
        lock (_sync)
        {
            var batch = Find(user, id);
            switch (action)
            {
                case "pause": batch.Paused = true; break;
                case "resume":
                    if (batch.Cancelled || batch.Entries.Any(e => e.State == "needs_verification")) throw new RotationException("uncertain_commit");
                    batch.Paused = false; break;
                case "cancel":
                    batch.Cancelled = true;
                    foreach (var entry in batch.Entries.Where(e => e.State == "queued" && e.JobId is null))
                    {
                        var accepted = byRequest(user, entry.RequestId);
                        if (accepted is null) entry.State = "cancelled";
                        else { entry.JobId = accepted.JobId; entry.State = "running"; }
                    }
                    break;
                case "retry":
                    if (batch.Entries.Any(e => e.State is "running" or "needs_verification")) throw new RotationException("file_busy");
                    foreach (var entry in batch.Entries.Where(e => e.State == "failed"))
                    { entry.State = "queued"; entry.RequestId = Guid.NewGuid(); entry.JobId = null; entry.Version = null; entry.ErrorCode = null; }
                    batch.Paused = false; batch.Cancelled = false; break;
                default: throw new RotationException("invalid_request");
            }
            Save(batch); return Status(batch, true, lookup);
        }
    }
    private RotationBatch Find(Guid user, Guid id)
    { RequireReady(); return _batches.FirstOrDefault(b => b.UserId == user && b.Id == id) ?? throw new RotationException("job_missing"); }
    private static string ShortTitle(string title) => string.Concat(title.EnumerateRunes().Take(128).Select(r => r.ToString()));
    private void RequireReady() { if (!_ready) throw new RotationException("server_preparing"); }
    // Called by one hosted dispatcher. A paused/cancelled batch still reconciles its current child.
    public void Tick(Func<Guid, BatchEntry, BatchPrepared> prepare, Func<Guid, Guid, JobStatus?> byRequest,
        Func<Guid, Guid, JobStatus?> byJob, Func<Guid, RotationRequest, JobStatus> submit)
    {
        lock (_sync)
        {
            RequireReady();
            foreach (var batch in _batches)
            foreach (var entry in batch.Entries.Where(e => e.State is "running" or "needs_verification"))
            {
                var job = entry.JobId is Guid id ? byJob(batch.UserId, id) : byRequest(batch.UserId, entry.RequestId);
                if (job is null) { batch.Paused = true; entry.State = "needs_verification"; entry.ErrorCode = "uncertain_commit"; Save(batch); continue; }
                var state = job.State == "completed" ? "completed" : job.State is "failed" or "recovery_failed" ? "failed" : job.State == "needs_verification" ? "needs_verification" : "running";
                if (entry.State != state || entry.ErrorCode != job.ErrorCode)
                {
                    entry.JobId = job.JobId; entry.State = state; entry.ErrorCode = job.ErrorCode;
                    if (state == "needs_verification" || job.ErrorCode is "insufficient_space" or "write_denied" or "permission_denied") batch.Paused = true;
                    Save(batch);
                }
                if (state == "running") return;
            }
            var next = _batches.OrderBy(b => b.CreatedAt).FirstOrDefault(b => !b.Paused && !b.Cancelled && b.Entries.Any(e => e.State == "queued"));
            if (next is null) return;
            var item = next.Entries.First(e => e.State == "queued");
            try
            {
                // Consult before resolving: the child may already have replaced the original.
                var job = byRequest(next.UserId, item.RequestId);
                if (job is null)
                {
                    if (item.Version is null)
                    {
                        var file = prepare(next.UserId, item);
                        item.MediaSourceId = file.MediaSourceId; item.Version = file.Version; item.Title = ShortTitle(file.Title);
                        Save(next);
                    }
                    job = submit(next.UserId, new RotationRequest(item.RequestId, item.ItemId, item.MediaSourceId!, item.Version!, next.Request.QuarterTurns));
                }
                item.JobId = job.JobId; item.State = "running"; Save(next);
            }
            catch (RotationException error)
            {
                if (error.Code is "server_preparing" or "queue_full") return;
                item.ErrorCode = error.Code; item.State = error.Code == "uncertain_commit" ? "needs_verification" : "failed";
                if (item.State == "needs_verification" || error.Code is "insufficient_space" or "write_denied" or "permission_denied") next.Paused = true;
                Save(next);
            }
            // IO/journal failures escape and stop dispatch. Never convert an ambiguous
            // accepted child into a retryable failure.
        }
    }
    public BatchEntryStatus[] ItemStates(Guid user, HashSet<string> ids)
    {
        lock (_sync) return _batches.Where(b => b.UserId == user).OrderByDescending(b => b.CreatedAt)
            .SelectMany(b => b.Entries).Where(e => ids.Contains(e.ItemId) && e.State is "queued" or "running" or "needs_verification")
            .DistinctBy(e => e.ItemId, StringComparer.OrdinalIgnoreCase).Select(e => Entry(e, null)).ToArray();
    }
    private static BatchEntryStatus Entry(BatchEntry entry, JobStatus? job) => new(entry.ItemId, entry.MediaSourceId, entry.Title, entry.State, entry.JobId, entry.ErrorCode, job?.Stage, job?.Progress ?? 0);
    private static BatchStatus Status(RotationBatch batch, bool detail, Func<Guid, Guid, JobStatus?>? lookup)
    {
        var entries = batch.Entries;
        var active = entries.FirstOrDefault(e => e.State == "running");
        BatchEntryStatus Convert(BatchEntry e) => Entry(e, e.JobId is Guid id ? lookup?.Invoke(batch.UserId, id) : null);
        var remaining = entries.Count(e => e.State is "queued" or "running");
        var blocked = entries.Count(e => e.State == "needs_verification");
        var state = active is not null ? (batch.Paused ? "pausing" : batch.Cancelled ? "cancelling" : "running")
            : blocked > 0 ? "needs_verification" : remaining == 0 ? batch.Cancelled ? "cancelled" : "finished" : batch.Paused ? "paused" : "queued";
        return new(batch.Id, batch.Request.RequestId, batch.Request.QuarterTurns, state, entries.Count,
            entries.Count(e => e.State == "completed"), entries.Count(e => e.State == "failed"), remaining,
            entries.Count(e => e.State == "cancelled"), blocked, batch.UpdatedAt, active is null ? null : Convert(active), detail ? entries.Select(Convert).ToArray() : null);
    }
}
