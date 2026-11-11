namespace Jellyfin.Plugin.GlideToolkit.Core;

/// All IO occurs on the server. The caller owns serialization and authorization.
public sealed class RotationProcessor(RotationStore store, IAtomicPublication? publication = null)
{
    private readonly IAtomicPublication _publication = publication ?? new AtomicPublication();
    public async Task Execute(RotationJob job, RotationJob? restore, CancellationToken token, Action? beforeReplace = null)
    {
        var file = RotationFiles.Validate(job.Path, job.Root);
        if (RotationFiles.Version(file) != job.Request.ExpectedVersion) throw new RotationException("file_changed");
        PublicationFileAccess.CheckAndRelease(file);
        var length = new FileInfo(file).Length;
        if (length > (long.MaxValue - 128 * 1024 * 1024) / 2) throw new RotationException("insufficient_space");
        var disk = new DriveInfo(Path.GetDirectoryName(file)!);
        if (disk.AvailableFreeSpace < 2 * length + 128 * 1024 * 1024) throw new RotationException("insufficient_space");
        var work = Path.Combine(Path.GetDirectoryName(file)!, ".glide-rotation");
        Directory.CreateDirectory(work); RotationFiles.CheckLink(work);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        job.PublicationMode = _publication.Probe(Path.GetDirectoryName(file)!, work);
        job.StageFile = job.PublicationMode == PublicationMode.Exchange ? Path.Combine(work, job.Id.ToString("N") + ".part")
            : Path.Combine(Path.GetDirectoryName(file)!, ".glide-rotation-" + job.Id.ToString("N") + ".part");
        job.BackupFile = Path.Combine(work, job.Id.ToString("N") + ".bak");
        if (File.Exists(job.StageFile) || File.Exists(job.BackupFile)) throw new RotationException("workspace_exists");
        // A crash before committing can be retried explicitly. No original bytes
        // are modified until a complete, verified stage and journal exist.
        Update(job, "running", "backing_up", 10);
        await using var original = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
        job.OriginalDigest = await RotationFiles.Digest(original, token).ConfigureAwait(false);
        if (restore is not null && (restore.State != "completed" || restore.Path != file || restore.SavedDigest != job.OriginalDigest)) throw new RotationException("restore_conflict");
        await RotationFiles.Copy(original, job.BackupFile, token).ConfigureAwait(false);
        if (await RotationFiles.DigestFile(job.BackupFile, token).ConfigureAwait(false) != job.OriginalDigest) throw new RotationException("backup_invalid");
        LinuxFileMetadata.Preserve(file, job.BackupFile);
        using (var durableBackup = File.OpenHandle(job.BackupFile, FileMode.Open, FileAccess.Read, FileShare.Read)) RandomAccess.FlushToDisk(durableBackup);
        job.BackupMetadataPreserved = true;
        Update(job, "running", "preparing", 40);
        VideoMatrix.Patch? patch = null;
        if (restore is null)
        {
            await RotationFiles.Copy(original, job.StageFile, token).ConfigureAwait(false);
            using var staged = new FileStream(job.StageFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            patch = VideoMatrix.Inspect(staged, job.Request.QuarterTurns); VideoMatrix.Apply(staged, patch); staged.Flush(true);
        }
        else
        {
            RotationFiles.CheckLink(restore.BackupFile);
            if (await RotationFiles.DigestFile(restore.BackupFile, token).ConfigureAwait(false) != restore.OriginalDigest) throw new RotationException("backup_invalid");
            await using var backup = new FileStream(restore.BackupFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            await RotationFiles.Copy(backup, job.StageFile, token).ConfigureAwait(false);
        }
        Update(job, "running", "verifying", 65);
        await using (var staged = new FileStream(job.StageFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            _ = VideoMatrix.Inspect(staged, 0);
            if (patch is not null && await RotationFiles.Digest(original, token, patch).ConfigureAwait(false) != await RotationFiles.Digest(staged, token, patch).ConfigureAwait(false)) throw new RotationException("verification_failed");
            job.SavedDigest = await RotationFiles.Digest(staged, token).ConfigureAwait(false);
        }
        // Do not keep our shared read lock across publication. On SMB this
        // lock can be mandatory and deny replacement after the target unlink.
        await original.DisposeAsync().ConfigureAwait(false);
        if (restore is not null && job.SavedDigest != restore.OriginalDigest) throw new RotationException("backup_invalid");
        LinuxFileMetadata.Preserve(file, job.StageFile);
        using (var durable = new FileStream(job.StageFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) durable.Flush(true);
        _ = RotationFiles.Validate(file, job.Root);
        if (RotationFiles.Version(file) != job.Request.ExpectedVersion || await RotationFiles.DigestFile(file, token).ConfigureAwait(false) != job.OriginalDigest) throw new RotationException("file_changed");
        token.ThrowIfCancellationRequested(); beforeReplace?.Invoke();
        PublicationFileAccess.CheckAndRelease(file);
        Durability.SyncDirectory(work);
        Durability.SyncDirectory(Path.GetDirectoryName(file)!);
        Update(job, "committing", "replacing", 85); // durable intent before rename
        if (job.PublicationMode == PublicationMode.Exchange) _publication.Exchange(job.StageFile, file);
        else _publication.RenameSameDirectory(job.StageFile, file);
        Durability.SyncDirectory(Path.GetDirectoryName(file)!);
        Durability.SyncDirectory(work);
        if (await RotationFiles.DigestFile(file, CancellationToken.None).ConfigureAwait(false) != job.SavedDigest) throw new RotationException("verification_failed");
        if (job.PublicationMode == PublicationMode.Exchange && await RotationFiles.DigestFile(job.StageFile, CancellationToken.None).ConfigureAwait(false) != job.OriginalDigest) throw new RotationException("verification_failed");
        job.RefreshPending = true;
        Update(job, "completed", "completed", 100);
    }
    public async Task Recover(RotationJob job, CancellationToken token)
    {
        // Restart reconciliation is read-only: never repeat a rotation because
        // a caller or server lost the response to the original operation.
        if (job.State is "completed" or "failed") return;
        try
        {
            _ = RotationFiles.Validate(job.Path, job.Root);
            if (job.State is "committing" or "needs_verification" && job.SavedDigest is not null && job.OriginalDigest is not null)
            {
                var digest = await RotationFiles.DigestFile(job.Path, token).ConfigureAwait(false);
                if (digest == job.SavedDigest && File.Exists(job.BackupFile) && await RotationFiles.DigestFile(job.BackupFile, token).ConfigureAwait(false) == job.OriginalDigest)
                { job.RefreshPending = true; Update(job, "completed", "completed", 100); return; }
                if (digest != job.OriginalDigest) { Update(job, "needs_verification", "needs_verification", 0, "uncertain_commit"); return; }
            }
            Update(job, "failed", "failed", 0, "interrupted_before_commit");
        }
        catch (OperationCanceledException) { throw; }
        catch { Update(job, "needs_verification", "needs_verification", 0, "uncertain_commit"); }
    }
    public void Fail(RotationJob job, Exception error)
    {
        job.FailureCode = error is RotationException detail ? detail.Code : error is UnauthorizedAccessException ? "write_denied" : error is IOException ? "io_failed" : "processing_failed";
        if (job.State == "committing") Update(job, "needs_verification", "needs_verification", job.Progress, "uncertain_commit");
        else Update(job, "failed", "failed", job.Progress, job.FailureCode);
    }
    private static void ValidateRecoveryPaths(RotationJob job)
    {
        _ = RotationFiles.Validate(job.Path, job.Root, allowMissing: true);
        var work = Path.Combine(Path.GetDirectoryName(job.Path)!, ".glide-rotation");
        RotationFiles.CheckLink(work);
        var expectedStage = job.PublicationMode == PublicationMode.Exchange ? Path.Combine(work, job.Id.ToString("N") + ".part")
            : Path.Combine(Path.GetDirectoryName(job.Path)!, ".glide-rotation-" + job.Id.ToString("N") + ".part");
        if (job.BackupFile != Path.Combine(work, job.Id.ToString("N") + ".bak")
            || job.StageFile != expectedStage
            || job.OriginalDigest?.Length != 64) throw new RotationException("backup_invalid");
        RotationFiles.CheckLink(job.BackupFile);
        if (File.Exists(job.StageFile)) RotationFiles.CheckLink(job.StageFile);
    }
    public async Task<RecoveryStatus> RecoveryInfo(RotationJob job, CancellationToken token)
    {
        ValidateRecoveryPaths(job);
        var exists = File.Exists(job.Path);
        var backup = File.Exists(job.BackupFile) && await RotationFiles.DigestFile(job.BackupFile, token).ConfigureAwait(false) == job.OriginalDigest;
        var restored = exists && await RotationFiles.DigestFile(job.Path, token).ConfigureAwait(false) == job.OriginalDigest;
        return new(job.Id, job.State, job.ErrorCode, job.FailureCode, exists, backup,
            !exists && backup && job.State is "needs_verification" or "recovery_failed", restored);
    }
    public async Task<RecoveryStatus> RecoverMissing(RotationJob job, CancellationToken token)
    {
        var info = await RecoveryInfo(job, token).ConfigureAwait(false);
        if (info.OriginalRestored)
        {
            // A manual NAS copy can already have repaired the missing path.
            // Reconcile that fact so the old uncertain task no longer blocks it.
            if (job.State is "needs_verification" or "recovery_failed")
                Update(job, "failed", "original_restored", 100, "original_restored");
            return await RecoveryInfo(job, token).ConfigureAwait(false);
        }
        if (info.OriginalExists) throw new RotationException("recovery_destination_exists");
        if (!info.CanRecoverMissing) throw new RotationException("backup_invalid");
        _publication.Probe(Path.GetDirectoryName(job.Path)!, Path.GetDirectoryName(job.BackupFile)!);
        var temporary = Path.Combine(Path.GetDirectoryName(job.BackupFile)!, job.Id.ToString("N") + ".recover-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var backup = new FileStream(job.BackupFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                await RotationFiles.Copy(backup, temporary, token).ConfigureAwait(false);
            if (await RotationFiles.DigestFile(temporary, token).ConfigureAwait(false) != job.OriginalDigest) throw new RotationException("backup_invalid");
            // The old rotated stage carries the original's ownership/ACLs.
            // Only trust it after matching the journal's recorded full digest.
            var validStage = File.Exists(job.StageFile) && await RotationFiles.DigestFile(job.StageFile, token).ConfigureAwait(false) == job.SavedDigest;
            if (!validStage && !job.BackupMetadataPreserved) throw new RotationException("metadata_unsupported");
            var metadata = validStage ? job.StageFile : job.BackupFile;
            LinuxFileMetadata.Preserve(metadata, temporary);
            using (var durable = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) durable.Flush(true);
            _ = RotationFiles.Validate(job.Path, job.Root, allowMissing: true);
            token.ThrowIfCancellationRequested();
            _publication.PublishMissing(temporary, job.Path); // fails if ANY destination appeared
            Durability.SyncDirectory(Path.GetDirectoryName(job.Path)!);
            Durability.SyncDirectory(Path.GetDirectoryName(job.BackupFile)!);
            if (await RotationFiles.DigestFile(job.Path, CancellationToken.None).ConfigureAwait(false) != job.OriginalDigest) throw new RotationException("verification_failed");
            job.RefreshPending = true;
            Update(job, "failed", "original_restored", 100, "original_restored");
            return await RecoveryInfo(job, CancellationToken.None).ConfigureAwait(false);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }
    private void Update(RotationJob job, string state, string stage, int progress, string? error = null)
    { job.State = state; job.Stage = stage; job.Progress = progress; job.ErrorCode = error; store.Save(job); }
}
