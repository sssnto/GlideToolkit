using Jellyfin.Plugin.GlideToolkit.Core;

static class FileActionChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        var source = Path.Combine(root, "file-actions"); var dest = Path.Combine(source, "destination"); Directory.CreateDirectory(dest);
        var store = new FileActionStore(Path.Combine(root, "file-action-journal")); var processor = new FileActionProcessor(store);
        FileActionBatch Batch(string name, bool sidecar = false)
        {
            var file = Path.Combine(source, name + ".mp4"); File.WriteAllBytes(file, [1,2,3,4,5]);
            var e = new FileActionEntry { ItemId = Guid.NewGuid().ToString("N"), Title = name, Root = source };
            e.Files.Add(new FilePair { Source = file, Target = Path.Combine(dest, Path.GetFileName(file)), Version = ManagedMediaFiles.Version(file) });
            if (sidecar) { var sub = Path.Combine(source, name + ".zh.ass"); File.WriteAllText(sub, "原创字幕"); e.Files.Add(new FilePair { Source = sub, Target = Path.Combine(dest, Path.GetFileName(sub)), Version = ManagedMediaFiles.Version(sub) }); }
            return new FileActionBatch { UserId = Guid.NewGuid(), Request = new(Guid.NewGuid(), "move", []), Entries = [e] };
        }
        var b = Batch("move", true); store.Save(b); processor.Execute(b, b.Entries[0], () => {});
        check(b.Entries[0].State == "completed" && b.Entries[0].Files.All(p => File.Exists(p.Target) && !File.Exists(p.Source)), "file-actions:video_and_same_name_subtitle_moved");
        check(File.ReadAllBytes(b.Entries[0].Files[0].Target).SequenceEqual(new byte[] {1,2,3,4,5}), "file-actions:move_preserves_all_bytes");
        var restore = new FileActionBatch { Entries = [new FileActionEntry { Root = source, Files = b.Entries[0].Files.Select(p => new FilePair { Source = p.Target, Target = p.Source, Version = p.Version }).ToList() }] };
        processor.Execute(restore, restore.Entries[0], () => {});
        check(restore.Entries[0].Files.All(p => File.Exists(p.Target) && !File.Exists(p.Source)), "file-actions:restore_exact_video_and_subtitle");
        File.WriteAllText(Path.Combine(source, "move-other.srt"), "unrelated");
        var bundled = ManagedMediaFiles.Bundle(b.Entries[0].Files[0].Source);
        check(bundled.Count == 2 && bundled.Contains(Path.Combine(source, "move.zh.ass")) && !bundled.Contains(Path.Combine(source, "move-other.srt")), "file-actions:bundle_detects_exact_and_language_suffix_subtitles_only");
        File.WriteAllBytes(Path.Combine(source, "move.part2.mp4"), [7,8,9]); File.WriteAllText(Path.Combine(source, "move.part2.zh.srt"), "another video");
        check(!ManagedMediaFiles.Bundle(b.Entries[0].Files[0].Source).Contains(Path.Combine(source, "move.part2.zh.srt")) && ManagedMediaFiles.Bundle(Path.Combine(source, "move.part2.mp4")).Contains(Path.Combine(source, "move.part2.zh.srt")), "file-actions:shared_prefix_does_not_take_another_videos_subtitle");
        var collision = Batch("collision"); File.WriteAllText(collision.Entries[0].Files[0].Target, "third-party");
        try { processor.Execute(collision, collision.Entries[0], () => {}); throw new Exception("Expected conflict"); } catch (RotationException e) { check(e.Code == "destination_exists", "file-actions:same_name_refused"); }
        check(File.ReadAllText(collision.Entries[0].Files[0].Target) == "third-party" && File.Exists(collision.Entries[0].Files[0].Source), "file-actions:conflict_keeps_both_sides");
        var partial = Batch("partial", true);
        try { new FileActionProcessor(store, new FailingFileRename()).Execute(partial, partial.Entries[0], () => {}); throw new Exception("Expected failure"); } catch (RotationException) { }
        check(partial.Entries[0].Files.All(p => File.Exists(p.Source) && !File.Exists(p.Target)) && partial.Entries[0].State == "failed", "file-actions:partial_bundle_rolls_back_without_copy");
        var changed = Batch("changed"); File.WriteAllText(changed.Entries[0].Files[0].Source, "new content");
        try { processor.Execute(changed, changed.Entries[0], () => {}); throw new Exception("Expected changed"); } catch (RotationException e) { check(e.Code == "file_changed", "file-actions:stale_confirmation_refused"); }
        var recovered = Batch("recover"); recovered.Entries[0].State = "running"; recovered.Entries[0].Files[0].State = "moving";
        new AtomicPublication().PublishMissing(recovered.Entries[0].Files[0].Source, recovered.Entries[0].Files[0].Target); store.Save(recovered);
        var loaded = store.Load().Single(x => x.Id == recovered.Id); processor.Recover(loaded, loaded.Entries[0]);
        check(loaded.Entries[0].State == "completed", "file-actions:restart_recognizes_already_moved_file");
        var pending = Batch("pending", true); pending.Entries[0].State = "running";
        new AtomicPublication().PublishMissing(pending.Entries[0].Files[0].Source, pending.Entries[0].Files[0].Target); processor.Recover(pending, pending.Entries[0]);
        check(pending.Entries[0].State == "failed" && pending.Entries[0].Files.All(p => File.Exists(p.Source)), "file-actions:restart_rolls_back_partial_bundle");
        var race = Batch("race"); race.Entries[0].State = "running"; race.Entries[0].Files[0].State = "moved";
        File.WriteAllText(race.Entries[0].Files[0].Target, "external"); processor.Recover(race, race.Entries[0]);
        check(race.Entries[0].State == "needs_verification" && File.ReadAllText(race.Entries[0].Files[0].Target) == "external", "file-actions:uncertain_restart_does_not_overwrite");
        var trash = Path.Combine(source, ".glide-trash"); ManagedMediaFiles.MakeRecycle(source, trash);
        var trashFile = Batch("trash"); trashFile.Entries[0].Files[0].Target = Path.Combine(trash, "saved.mp4"); processor.Execute(trashFile, trashFile.Entries[0], () => {});
        check(File.Exists(trashFile.Entries[0].Files[0].Target), "file-actions:deletion_is_recoverable_rename");
        var controls = new FileActionBatch { Entries = [new() { State = "completed" }, new() { State = "running" }, new() { State = "queued" }] };
        controls.Change("pause"); check(controls.State == "paused" && controls.Entries[1].State == "running", "file-actions:pause_does_not_interrupt_running_file");
        controls.Change("resume"); controls.Change("cancel");
        check(controls.Entries[0].State == "completed" && controls.Entries[1].State == "running" && controls.Entries[2].State == "cancelled", "file-actions:cancel_only_unstarted_items");
        controls.Entries[1].State = "failed"; controls.Entries[1].ErrorCode = "active_playback"; controls.Change("retry");
        check(controls.Entries[0].State == "completed" && controls.Entries[1].State == "queued" && controls.Entries[1].ErrorCode is null && controls.Entries[2].State == "cancelled", "file-actions:retry_only_failed_without_repeating_successes");
        var unknown = new FileActionBatch { Entries = [new() { State = "needs_verification" }, new() { State = "failed" }] };
        try { unknown.Change("retry"); throw new Exception("Expected verification stop"); } catch (RotationException e) { check(e.Code == "invalid_action", "file-actions:uncertain_results_cannot_be_blindly_retried"); }
        var link = Path.Combine(source, "link.mp4"); File.CreateSymbolicLink(link, trashFile.Entries[0].Files[0].Target);
        try { ManagedMediaFiles.Validate(source, link); throw new Exception("Expected link refusal"); } catch (RotationException e) { check(e.Code == "symbolic_link", "file-actions:symlinks_refused"); }
        check(!ManagedMediaFiles.Within(source, source + "-other/file.mp4"), "file-actions:sibling_prefix_is_not_in_scope");
        check(ManagedMediaFiles.IsVideo("movie.MKV") && !ManagedMediaFiles.IsVideo("poster.jpg"), "file-actions:file_management_formats_independent_of_rotation");
    }
    private sealed class FailingFileRename : IAtomicPublication
    {
        private readonly AtomicPublication _native = new(); private int calls;
        public PublicationMode Probe(string a, string b) => throw new NotSupportedException();
        public void Exchange(string a, string b) => throw new NotSupportedException();
        public void RenameSameDirectory(string a, string b) => throw new NotSupportedException();
        public void PublishMissing(string a, string b) { if (++calls == 2) throw new RotationException("atomic_publish_18"); _native.PublishMissing(a,b); }
    }
}
