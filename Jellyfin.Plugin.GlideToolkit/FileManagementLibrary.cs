using System.Security.Claims;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Data;
using Jellyfin.Plugin.GlideToolkit.Core;

namespace Jellyfin.Plugin.GlideToolkit;

public sealed record ManagedVideo(Video Item, string SourceId, string Root, string Path);
public sealed record ManagedFolder(string Id, string Name, string? Root, string? Path, bool CanChoose);
public sealed class FileManagementLibrary(ILibraryManager library, IMediaSourceManager media, IUserManager users,
    ISessionManager sessions, IFileSystem filesystem)
{
    public bool Allowed(Guid userId)
    {
        var user = users.GetUserById(userId); var config = Plugin.Instance?.Configuration;
        return OperatingSystem.IsLinux() && config?.FileManagementEnabled == true && user is not null && !user.HasPermission(PermissionKind.IsDisabled)
            && (user.HasPermission(PermissionKind.IsAdministrator) || (config.FileManagementUserIds ?? []).Any(value => Guid.TryParse(value, out var id) && id == userId));
    }
    public void Require(Guid userId) { if (!Allowed(userId)) throw new RotationException("file_management_disabled"); }
    private string Root(string path)
    {
        var roots = library.GetVirtualFolders().SelectMany(f => f.Locations ?? []).Where(root => ManagedMediaFiles.Within(root, path, equal: true)).OrderByDescending(s => s.Length).ToArray();
        if (roots.Length == 0) throw new RotationException("outside_library");
        var allowed = Plugin.Instance?.Configuration.AllowedRoots ?? [];
        if (allowed.Length > 0 && !allowed.Any(root => ManagedMediaFiles.Within(root, path, equal: true))) throw new RotationException("outside_library");
        return roots[0];
    }
    public ManagedVideo Resolve(Guid userId, string itemId, string? sourceId = null)
    {
        Require(userId);
        if (!Guid.TryParse(itemId, out var id)) throw new RotationException("invalid_item");
        var item = library.GetItemById<Video>(id, userId) ?? throw new RotationException("item_missing");
        // File-level management must not delete series/movie folders or an ambiguous multi-version item.
        if (item.GetType() != typeof(Video)) throw new RotationException("home_video_only");
        var user = users.GetUserById(userId)!;
        var sources = media.GetStaticMediaSources(item, false, user).Where(s => s.Protocol == MediaProtocol.File).ToArray();
        if (sources.Length != 1 || sources[0].Path != item.Path || (sourceId is not null && RotationLibrary.ItemKey(sources[0].Id) != RotationLibrary.ItemKey(sourceId))) throw new RotationException("version_selection_required");
        var path = Path.GetFullPath(sources[0].Path); var root = Root(path);
        if (!ManagedMediaFiles.IsVideo(path)) throw new RotationException("unsupported_format");
        ManagedMediaFiles.Validate(root, path);
        return new(item, sources[0].Id, root, path);
    }
    public ManagedFolder Folder(Guid userId, string id, bool choose)
    {
        Require(userId);
        if (!Guid.TryParse(id, out var guid)) throw new RotationException("invalid_folder");
        var folder = library.GetItemById<Folder>(guid, userId) ?? throw new RotationException("permission_denied");
        string? root = null; string? path = null;
        try { if (!string.IsNullOrEmpty(folder.Path) && Directory.Exists(folder.Path)) { path = Path.GetFullPath(folder.Path); root = Root(path); ManagedMediaFiles.Validate(root, path, directory: true); } }
        catch (RotationException) { path = null; root = null; }
        if (choose && path is null) throw new RotationException("destination_not_writable");
        return new(folder.Id.ToString("N"), folder.Name, root, path, path is not null);
    }
    public object Folders(Guid userId, string? parent, int start)
    {
        Require(userId); if (start < 0 || start > 100000) throw new RotationException("invalid_request");
        var user = users.GetUserById(userId)!;
        var folder = parent is null ? library.GetUserRootFolder() : library.GetItemById<Folder>(Guid.Parse(parent), userId) ?? throw new RotationException("permission_denied");
        var children = folder.GetChildren(user, true).OfType<Folder>().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var page = children.Skip(start).Take(100).Select(f => Folder(userId, f.Id.ToString("N"), false)).ToArray();
        var current = parent is null ? null : Folder(userId, parent, false);
        return new { current = current is null ? null : new { id = current.Id, name = current.Name, canChoose = current.CanChoose },
            folders = page.Select(f => new { id = f.Id, name = f.Name, canChoose = f.CanChoose }), nextIndex = start + page.Length, total = children.Length };
    }
    public bool Playing(string path) => sessions.Sessions.Any(s => s.NowPlayingItem is not null && s.NowPlayingItem.Path == path);
    public void ValidateRecorded(Guid userId, FileActionEntry entry, string? destinationId, bool restore, bool verifySource = true)
    {
        Require(userId);
        foreach (var path in entry.Files.SelectMany(p => new[] { p.Source, p.Target }))
        {
            if (Root(path) != entry.Root) throw new RotationException("outside_library");
            var parent = Path.GetDirectoryName(path)!;
            // Recycle is not indexed. Validate the owning visible folder instead.
            var marker = parent.IndexOf(Path.DirectorySeparatorChar + ".glide-trash", StringComparison.Ordinal);
            if (marker >= 0) parent = parent[..marker];
            var folder = library.FindByPath(parent, true);
            if (folder is null || library.GetItemById<Folder>(folder.Id, userId) is null) throw new RotationException("permission_denied");
        }
        if (!restore && verifySource)
        {
            var source = Resolve(userId, entry.ItemId, entry.MediaSourceId);
            if (source.Path != entry.Files[0].Source || source.Root != entry.Root) throw new RotationException("source_changed");
            if (destinationId is not null && Folder(userId, destinationId, true).Path != Path.GetDirectoryName(entry.Files[0].Target)) throw new RotationException("destination_changed");
        }
        if (entry.Files.Any(p => Playing(p.Source) || Playing(p.Target))) throw new RotationException("active_playback");
    }
    public async Task Refresh(IEnumerable<FileActionEntry> entries, CancellationToken token)
    {
        var paths = entries.SelectMany(e => e.Files.SelectMany(p => new[] { Path.GetDirectoryName(p.Source)!, Path.GetDirectoryName(p.Target)! })).Select(path => {
            var marker = path.IndexOf(Path.DirectorySeparatorChar + ".glide-trash", StringComparison.Ordinal);
            return marker >= 0 ? path[..marker] : path;
        }).Distinct().ToArray();
        foreach (var path in paths)
        {
            if (library.FindByPath(path, true) is not Folder folder) continue;
            var options = new MetadataRefreshOptions(new DirectoryService(filesystem)) {
                MetadataRefreshMode = MetadataRefreshMode.Default, ImageRefreshMode = MetadataRefreshMode.Default,
                ReplaceAllMetadata = false, ReplaceAllImages = false, IsAutomated = false
            };
            await folder.ValidateChildren(new Progress<double>(), options, recursive: false, cancellationToken: token).ConfigureAwait(false);
        }
    }
}
