using Jellyfin.Plugin.GlideToolkit.Core;

static class BatchChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        var user = Guid.NewGuid();
        var worker = new Worker();
        var path = Path.Combine(root, "batches");
        RotationBatches Open() { var value = new RotationBatches(path); value.Load(); return value; }
        var queue = Open();
        BatchRequest Request(int count) => new(Guid.NewGuid(), 1, Enumerable.Range(0, count).Select(_ => new BatchItemRequest(Guid.NewGuid().ToString("N"), null, "Authored fixture")).ToArray());
        void Tick(RotationBatches? q = null) => (q ?? queue).Tick(worker.Prepare, worker.ByRequest, worker.ByJob, worker.Submit);
        var request = Request(3); var batch = queue.Submit(user, request);
        check(queue.Submit(user, request).BatchId == batch.BatchId, "batch_submit_idempotent");
        Reject(() => queue.Submit(user, request with { QuarterTurns = 2 }), "request_conflict", check);
        Reject(() => queue.Submit(user, request with { RequestId = Guid.NewGuid() }), "file_busy", check);
        Reject(() => queue.Submit(user, new(Guid.NewGuid(), 1, [request.Items[0] with { ItemId = Guid.Parse(request.Items[0].ItemId).ToString("D") }])), "file_busy", check);
        Reject(() => queue.Submit(user, Request(1001)), "invalid_batch", check);
        var duplicate = request.Items[0];
        Reject(() => queue.Submit(user, new(Guid.NewGuid(), 1, [duplicate, duplicate with { ItemId = Guid.Parse(duplicate.ItemId).ToString("D") }])), "invalid_batch", check);
        Tick(); var first = queue.Get(user, batch.BatchId, worker.ByJob);
        check(worker.Submissions == 1 && first.Current is not null && first.Remaining == 3, "batch_only_one_child_dispatched");
        Tick(); Tick(); check(worker.Submissions == 1, "batch_never_parallelizes_large_files");
        queue.Change(user, batch.BatchId, "pause", worker.ByJob, worker.ByRequest);
        check(queue.Get(user, batch.BatchId, worker.ByJob).State == "pausing", "pause_waits_for_current_file");
        worker.Complete(first.Current!.JobId!.Value); Tick(); Tick();
        check(worker.Submissions == 1 && queue.Get(user, batch.BatchId, worker.ByJob).State == "paused", "paused_batch_finishes_current_but_not_next");
        queue = Open(); Tick(); check(worker.Submissions == 1, "pause_survives_server_restart");
        queue.Change(user, batch.BatchId, "resume", worker.ByJob, worker.ByRequest); Tick();
        var second = queue.Get(user, batch.BatchId, worker.ByJob).Current!;
        queue.Change(user, batch.BatchId, "cancel", worker.ByJob, worker.ByRequest);
        worker.Complete(second.JobId!.Value); Tick();
        var cancelled = queue.Get(user, batch.BatchId, worker.ByJob);
        check(cancelled.Completed == 2 && cancelled.Cancelled == 1 && cancelled.Remaining == 0, "cancel_keeps_completed_and_current_files");
        var crashRequest = Request(2); var crashBatch = queue.Submit(user, crashRequest);
        worker.DropResponse = true;
        try { Tick(); throw new Exception("expected_lost_response"); } catch (IOException) { }
        var accepted = worker.Submissions;
        queue = Open(); Tick();
        check(worker.Submissions == accepted && queue.Get(user, crashBatch.BatchId, worker.ByJob).Current is not null, "restart_recovers_accepted_child_without_duplicate_rotation");
        var active = queue.Get(user, crashBatch.BatchId, worker.ByJob).Current!;
        worker.Complete(active.JobId!.Value, "failed", "active_playback"); Tick();
        active = queue.Get(user, crashBatch.BatchId, worker.ByJob).Current!;
        worker.Complete(active.JobId!.Value); Tick();
        var beforeRetry = worker.Submissions;
        queue.Change(user, crashBatch.BatchId, "retry", worker.ByJob, worker.ByRequest); Tick();
        active = queue.Get(user, crashBatch.BatchId, worker.ByJob).Current!;
        check(worker.Submissions == beforeRetry + 1 && queue.Get(user, crashBatch.BatchId, worker.ByJob).Completed == 1, "retry_failed_only_never_repeats_completed");
        worker.Complete(active.JobId!.Value); Tick();
        check(queue.Get(user, crashBatch.BatchId, worker.ByJob).Completed == 2, "retry_reaches_complete_counts");
        var uncertain = queue.Submit(user, Request(2)); Tick();
        active = queue.Get(user, uncertain.BatchId, worker.ByJob).Current!;
        worker.Complete(active.JobId!.Value, "needs_verification", "uncertain_commit"); Tick();
        var blockedCount = worker.Submissions; Tick();
        check(worker.Submissions == blockedCount && queue.Get(user, uncertain.BatchId, worker.ByJob).NeedsVerification == 1, "uncertain_commit_pauses_remaining_files");
        Reject(() => queue.Change(user, uncertain.BatchId, "resume", worker.ByJob, worker.ByRequest), "uncertain_commit", check);
        Reject(() => queue.Change(user, uncertain.BatchId, "retry", worker.ByJob, worker.ByRequest), "file_busy", check);
        worker.Complete(active.JobId!.Value); Tick();
        check(queue.Get(user, uncertain.BatchId, worker.ByJob).State == "paused", "verified_child_requires_explicit_resume");
        queue.Change(user, uncertain.BatchId, "cancel", worker.ByJob, worker.ByRequest); Tick();
        var lostCancel = queue.Submit(user, Request(2)); worker.DropResponse = true;
        try { Tick(); } catch (IOException) { }
        var cancelStatus = queue.Change(user, lostCancel.BatchId, "cancel", worker.ByJob, worker.ByRequest);
        check(cancelStatus.Current is not null && cancelStatus.Cancelled == 1, "cancel_after_lost_response_still_tracks_accepted_child");
        worker.Complete(cancelStatus.Current!.JobId!.Value); Tick();
        var final = queue.Get(user, lostCancel.BatchId, worker.ByJob);
        check(final.Completed == 1 && final.Cancelled == 1 && final.Total == final.Completed + final.Failed + final.Remaining + final.Cancelled + final.NeedsVerification, "batch_counts_partition_all_entries");
        Reject(() => queue.Get(Guid.NewGuid(), lostCancel.BatchId, worker.ByJob), "job_missing", check);
        check(queue.List(Guid.NewGuid(), worker.ByJob).Length == 0, "batch_history_is_user_scoped");
        var big = queue.Submit(user, Request(1000));
        check(big.Total == 1000 && big.Remaining == 1000 && worker.Submissions == blockedCount + 1, "thousand_item_acceptance_does_not_start_thousand_workers");
        var reloaded = Open();
        check(reloaded.Get(user, big.BatchId, worker.ByJob).Total == 1000, "thousand_item_queue_survives_restart");
        var blockedDirectory = Path.Combine(root, "unwritable-ledger");
        var blocked = new RotationBatches(blockedDirectory); blocked.Load();
        Directory.Delete(blockedDirectory); File.WriteAllText(blockedDirectory, "authored fixture blocks directory");
        try { blocked.Submit(user, Request(1)); throw new Exception("expected_write_failure"); } catch (IOException) { }
        check(!blocked.Ready, "journal_write_failure_disables_new_dispatch_until_reload");
        var corrupt = Path.Combine(root, "broken-batch-journal"); Directory.CreateDirectory(corrupt);
        File.WriteAllText(Path.Combine(corrupt, "bad.json"), "{bad");
        var broken = new RotationBatches(corrupt);
        try { broken.Load(); throw new Exception("expected_invalid_ledger"); } catch (System.Text.Json.JsonException) { }
        check(!broken.Ready, "damaged_batch_journal_fails_closed");
    }
    private static void Reject<T>(Func<T> action, string code, Action<bool, string> check)
    {
        try { action(); throw new Exception("expected_" + code); }
        catch (RotationException error) { check(error.Code == code, "batch_reject_" + code); }
    }
    private sealed class Worker
    {
        private readonly Dictionary<Guid, JobStatus> _requests = [];
        public int Submissions;
        public bool DropResponse;
        public BatchPrepared Prepare(Guid user, BatchEntry entry) => new(entry.ItemId, entry.ItemId, "fixture-version", entry.Title);
        public JobStatus? ByRequest(Guid user, Guid request) => _requests.GetValueOrDefault(request);
        public JobStatus? ByJob(Guid user, Guid id) => _requests.Values.FirstOrDefault(j => j.JobId == id);
        public JobStatus Submit(Guid user, RotationRequest request)
        {
            if (_requests.TryGetValue(request.RequestId, out var prior)) return prior;
            Submissions++;
            var job = new JobStatus(Guid.NewGuid(), request.RequestId, request.ItemId, request.MediaSourceId, "running", "backing_up", 10, null, false, false, DateTime.UtcNow, "SameDirectoryRename", null);
            _requests.Add(request.RequestId, job);
            if (DropResponse) { DropResponse = false; throw new IOException("lost_response"); }
            return job;
        }
        public void Complete(Guid id, string state = "completed", string? error = null)
        {
            var job = _requests.Values.First(j => j.JobId == id);
            _requests[job.RequestId] = job with { State = state, Stage = state, ErrorCode = error, Progress = state == "completed" ? 100 : 10 };
        }
    }
}
