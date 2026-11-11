using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.GlideToolkit.Core;

var root = Path.Combine(Path.GetTempPath(), "GlideToolkit-Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var fixture = args[0];
var passed = new List<string>();
void Check(bool result, string name) { if (!result) throw new Exception(name); passed.Add(name); }
async Task Reject(Func<Task> action, string code)
{
    try { await action(); throw new Exception("Expected " + code); }
    catch (RotationException error) { Check(error.Code == code, "reject_" + code); }
}
RotationJob Job(string file, Guid? restore = null) => new()
{
    Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Path = file, Root = root,
    Request = new(Guid.NewGuid(), Guid.NewGuid().ToString("N"), "source", RotationFiles.Version(file), restore is null ? 1 : 0, restore)
};
string Sample(string name) { var file = Path.Combine(root, name + ".mp4"); File.Copy(fixture, file); return file; }
try
{
    var file = Sample("rotation"); var original = await File.ReadAllBytesAsync(file);
    var store = new RotationStore(Path.Combine(root, "journal")); var processor = new RotationProcessor(store);
    var first = Job(file); await processor.Execute(first, null, CancellationToken.None);
    Check(first.State == "completed", "rotation_completed");
    Check((await File.ReadAllBytesAsync(first.BackupFile)).SequenceEqual(original), "backup_byte_identical");
    var rotated = await File.ReadAllBytesAsync(file);
    using (var stream = new MemoryStream(original))
    {
        var patch = VideoMatrix.Inspect(stream, 1);
        Check(rotated.Length == original.Length, "file_size_unchanged");
        Check(original.Where((_, i) => i < patch.Offset || i >= patch.Offset + 36).SequenceEqual(rotated.Where((_, i) => i < patch.Offset || i >= patch.Offset + 36)), "all_bytes_outside_matrix_identical");
    }
    var restored = Job(file, first.Id); await processor.Execute(restored, first, CancellationToken.None);
    Check((await File.ReadAllBytesAsync(file)).SequenceEqual(original), "restore_exact_original");
    Check((await File.ReadAllBytesAsync(restored.BackupFile)).SequenceEqual(rotated), "restore_preserves_previous_version");
    var changed = Sample("changed"); var stale = Job(changed); File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddSeconds(3));
    await Reject(() => processor.Execute(stale, null, CancellationToken.None), "file_changed");
    Check((await File.ReadAllBytesAsync(changed)).SequenceEqual(original), "stale_request_keeps_original");
    var noCommit = Sample("interrupted"); var crash = Job(noCommit);
    await Reject(() => processor.Execute(crash, null, CancellationToken.None, () => throw new RotationException("test_interrupt")), "test_interrupt");
    Check((await File.ReadAllBytesAsync(noCommit)).SequenceEqual(original), "interrupted_before_commit_keeps_original");
    crash.State = "committing"; store.Save(crash);
    await new RotationProcessor(new RotationStore(Path.Combine(root, "journal"))).Recover(crash, CancellationToken.None);
    Check(crash.State == "failed", "restart_does_not_repeat_uncommitted_rotation");
    first.State = "committing"; File.WriteAllBytes(file, rotated); store.Save(first);
    await processor.Recover(first, CancellationToken.None);
    Check(first.State == "completed", "restart_recognizes_already_committed_output");
    first.State = "committing"; var thirdParty = rotated.ToArray(); thirdParty[^1] ^= 1; File.WriteAllBytes(file, thirdParty);
    await processor.Recover(first, CancellationToken.None);
    Check(first.State == "needs_verification" && (await File.ReadAllBytesAsync(file)).SequenceEqual(thirdParty), "ambiguous_restart_is_read_only");
    await Reject(() => { RotationFiles.Validate(fixture, root); return Task.CompletedTask; }, "outside_library");
    var link = Path.Combine(root, "link.mp4"); File.CreateSymbolicLink(link, fixture);
    await Reject(() => { RotationFiles.Validate(link, root); return Task.CompletedTask; }, "symbolic_link");
    var cancelFile = Sample("cancel"); var cancelled = Job(cancelFile); using var token = new CancellationTokenSource(); token.Cancel();
    try { await processor.Execute(cancelled, null, token.Token); } catch (OperationCanceledException) { }
    Check((await File.ReadAllBytesAsync(cancelFile)).SequenceEqual(original), "cancelled_before_commit_keeps_original");

    var unsupportedFile = Sample("unsupported-storage"); var unsupported = Job(unsupportedFile);
    await Reject(() => new RotationProcessor(store, new FaultPublication(failProbe: true)).Execute(unsupported, null, CancellationToken.None), "atomic_replace_unsupported");
    Check((await File.ReadAllBytesAsync(unsupportedFile)).SequenceEqual(original) && string.IsNullOrEmpty(unsupported.BackupFile), "unsupported_filesystem_refuses_before_backup_or_media_modification");
    var refusedFile = Sample("exchange-refused"); var refused = Job(refusedFile);
    await Reject(() => new RotationProcessor(store, new FaultPublication(failExchange: true)).Execute(refused, null, CancellationToken.None), "atomic_exchange_95");
    Check((await File.ReadAllBytesAsync(refusedFile)).SequenceEqual(original), "exchange_failure_never_unlinks_original");
    Check((await File.ReadAllBytesAsync(refused.BackupFile)).SequenceEqual(original) && File.Exists(refused.StageFile), "exchange_failure_retains_backup_and_stage");

    // Model the observed 1.0.0 incident without touching any private file.
    var missingFile = Sample("legacy-missing"); var missing = Job(missingFile);
    await Reject(() => processor.Execute(missing, null, CancellationToken.None, () => throw new RotationException("test_interrupt")), "test_interrupt");
    missing.State = "needs_verification"; missing.ErrorCode = "uncertain_commit"; store.Save(missing);
    File.Delete(missingFile);
    var missingInfo = await processor.RecoveryInfo(missing, CancellationToken.None);
    Check(missingInfo.CanRecoverMissing && !missingInfo.OriginalExists, "missing_original_detected_from_journal_without_library_item");
    var result = await processor.RecoverMissing(missing, CancellationToken.None);
    Check(result.OriginalRestored && (await File.ReadAllBytesAsync(missingFile)).SequenceEqual(original), "legacy_missing_original_recovered_byte_identically");
    Check((await File.ReadAllBytesAsync(missing.BackupFile)).SequenceEqual(original) && File.Exists(missing.StageFile), "recovery_retains_both_legacy_evidence_files");
    await processor.RecoverMissing(missing, CancellationToken.None);
    Check((await File.ReadAllBytesAsync(missingFile)).SequenceEqual(original), "repeated_recovery_is_idempotent");
    File.WriteAllBytes(missingFile, thirdParty);
    await Reject(() => processor.RecoverMissing(missing, CancellationToken.None), "recovery_destination_exists");
    Check((await File.ReadAllBytesAsync(missingFile)).SequenceEqual(thirdParty), "recovery_never_overwrites_existing_third_party_file");
    File.Delete(missingFile); missing.State = "needs_verification";
    await Reject(() => new RotationProcessor(store, new FaultPublication(raceBytes: thirdParty)).RecoverMissing(missing, CancellationToken.None), OperatingSystem.IsMacOS() ? "atomic_publish_17" : "atomic_publish_17");
    Check((await File.ReadAllBytesAsync(missingFile)).SequenceEqual(thirdParty), "destination_created_during_recovery_is_not_overwritten");
    File.Delete(missingFile);
    var backupBytes = await File.ReadAllBytesAsync(missing.BackupFile); backupBytes[^1] ^= 1; File.WriteAllBytes(missing.BackupFile, backupBytes);
    await Reject(() => processor.RecoverMissing(missing, CancellationToken.None), "backup_invalid");
    Check(!File.Exists(missingFile), "corrupt_backup_is_never_published");
    File.WriteAllBytes(missing.BackupFile, original); File.WriteAllBytes(missingFile, original);
    var repaired = await processor.RecoverMissing(missing, CancellationToken.None);
    Check(repaired.OriginalRestored && missing.State == "failed" && missing.Stage == "original_restored", "manual_copy_recovery_reconciles_uncertain_job");
    var sameFile = Sample("same-directory"); var same = Job(sameFile);
    var sameProcessor = new RotationProcessor(store, new FaultPublication(forceSameDirectory: true));
    await sameProcessor.Execute(same, null, CancellationToken.None, () =>
        Check(Path.GetDirectoryName(same.StageFile) == Path.GetDirectoryName(sameFile), "fallback_stage_is_exactly_in_original_parent"));
    Check(same.State == "completed" && same.PublicationMode == PublicationMode.SameDirectoryRename, "same_directory_native_rename_completes");
    Check(!File.Exists(same.StageFile) && (await File.ReadAllBytesAsync(same.BackupFile)).SequenceEqual(original), "same_directory_rename_consumes_stage_and_retains_exact_backup");
    Check((await File.ReadAllBytesAsync(sameFile)).SequenceEqual(rotated), "fallback_changes_only_expected_matrix");
    var sameRestore = Job(sameFile, same.Id); await sameProcessor.Execute(sameRestore, same, CancellationToken.None);
    Check((await File.ReadAllBytesAsync(sameFile)).SequenceEqual(original), "same_directory_native_restore_is_exact");
    Check(store.Load().Single(j => j.Id == same.Id).PublicationMode == PublicationMode.SameDirectoryRename, "publication_mode_survives_durable_journal_reload");
    var exdevFile = Sample("same-directory-refused"); var exdev = Job(exdevFile);
    await Reject(() => new RotationProcessor(store, new FaultPublication(forceSameDirectory: true, failExchange: true)).Execute(exdev, null, CancellationToken.None), "atomic_rename_18");
    Check((await File.ReadAllBytesAsync(exdevFile)).SequenceEqual(original) && File.Exists(exdev.StageFile), "EXDEV_never_falls_back_to_overwrite_copy");
    await Reject(() => { new AtomicPublication().RenameSameDirectory(exdev.StageFile, Path.Combine(root, "other", "destination.mp4")); return Task.CompletedTask; }, "atomic_cross_directory_refused");
    var absent = Path.Combine(root, "absent-part");
    await Reject(() => { new AtomicPublication().RenameSameDirectory(absent, exdevFile); return Task.CompletedTask; }, "atomic_rename_2");
    Check((await File.ReadAllBytesAsync(exdevFile)).SequenceEqual(original), "native_rename_failure_keeps_existing_destination");
    same.State = "committing"; File.WriteAllBytes(sameFile, rotated);
    await sameProcessor.Recover(same, CancellationToken.None);
    Check(same.State == "completed", "restart_recognizes_same_directory_committed_output");
    // CIFS/SMB locks are not necessarily advisory. The actual publication must
    // not retain our original read lock, unlike the old closed-file probes.
    var handlesFile = Sample("closed-before-publication"); var handles = Job(handlesFile);
    var closedProcessor = new RotationProcessor(store, new FaultPublication(forceSameDirectory: true, requireClosedOriginal: true));
    await closedProcessor.Execute(handles, null, CancellationToken.None);
    Check(handles.State == "completed", "all_owned_original_handles_are_closed_before_publication");
    var busyFile = Sample("busy-file"); var busy = Job(busyFile);
    using (var otherReader = new FileStream(busyFile, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        await Reject(() => processor.Execute(busy, null, CancellationToken.None), "file_busy");
        Check(string.IsNullOrEmpty(busy.BackupFile), "busy_file_refused_before_backup_or_publication");
    }
    Check((await File.ReadAllBytesAsync(busyFile)).SequenceEqual(original), "busy_file_keeps_original_byte_identical");
    var exchangedHandlesFile = Sample("closed-before-exchange"); var exchangedHandles = Job(exchangedHandlesFile);
    await new RotationProcessor(store, new FaultPublication(requireClosedOriginal: true)).Execute(exchangedHandles, null, CancellationToken.None);
    Check(exchangedHandles.State == "completed", "exchange_also_releases_owned_original_handles");
    var completionTime = restored.CompletedAt;
    Check(completionTime is not null, "completed_jobs_record_stable_content_save_time");
    store.Save(restored);
    Check(restored.CompletedAt == completionTime, "later_metadata_journal_updates_do_not_reorder_saved_previews");
    Check(store.Load().First(j => j.Id == restored.Id).CompletedAt == completionTime, "saved_preview_order_survives_restart");
    BatchChecks.Run(root, Check);
    var output = args.Length > 1 ? args[1] : Path.Combine(root, "report.json");
    File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, checks = passed, timestamp = DateTime.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Passed {passed.Count} server rotation checks");
}
finally { Directory.Delete(root, true); }

sealed class FaultPublication(bool failProbe = false, bool failExchange = false, byte[]? raceBytes = null, bool forceSameDirectory = false, bool requireClosedOriginal = false) : IAtomicPublication
{
    private readonly AtomicPublication _native = new();
    public PublicationMode Probe(string parent, string work) { if (failProbe) throw new RotationException("atomic_replace_unsupported"); if (forceSameDirectory) { _native.ProbeSameDirectory(parent); return PublicationMode.SameDirectoryRename; } return _native.Probe(parent, work); }
    public void Exchange(string staged, string original)
    {
        if (failExchange) throw new RotationException("atomic_exchange_95");
        if (requireClosedOriginal) { using var exclusive = new FileStream(original, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        _native.Exchange(staged, original);
    }
    public void RenameSameDirectory(string staged, string original)
    {
        if (failExchange) throw new RotationException("atomic_rename_18");
        if (requireClosedOriginal) { using var exclusive = new FileStream(original, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        _native.RenameSameDirectory(staged, original);
    }
    public void PublishMissing(string staged, string destination)
    {
        if (raceBytes is not null) File.WriteAllBytes(destination, raceBytes);
        _native.PublishMissing(staged, destination);
    }
}
