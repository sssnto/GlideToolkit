using System.Security.Claims;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Jellyfin.Plugin.GlideToolkit.Core;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;

namespace Jellyfin.Plugin.GlideToolkit;

public sealed record ResolvedVideo(Guid UserId, Video Item, string SourceId, string Path, string Root);
public sealed class RotationLibrary(ILibraryManager library, IMediaSourceManager media, IUserManager users,
    ISessionManager sessions, IProviderManager providers, IFileSystem filesystem)
{
    public static string ItemKey(string value) => Guid.TryParse(value, out var id) ? id.ToString("N") : value.ToLowerInvariant();
    public static Guid UserId(ClaimsPrincipal user)
    {
        var value = user.Claims.FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        return Guid.TryParse(value, out var id) ? id : throw new RotationException("permission_denied");
    }
    public bool Allowed(ClaimsPrincipal user)
    {
        var config = Plugin.Instance?.Configuration;
        return config?.Enabled == true && (user.IsInRole("Administrator") || config.AllowedUserIds.Any(value => Guid.TryParse(value, out var allowed) && allowed == UserId(user)));
    }
    public ResolvedVideo Resolve(ClaimsPrincipal user, string itemId, string sourceId)
    {
        if (!OperatingSystem.IsLinux()) throw new RotationException("platform_unsupported");
        if (!Allowed(user)) throw new RotationException("permission_denied");
        var userId = UserId(user);
        return Resolve(userId, itemId, sourceId);
    }
    public ResolvedVideo Resolve(Guid userId, string itemId, string sourceId)
    {
        if (!Guid.TryParse(itemId, out var id) || string.IsNullOrWhiteSpace(sourceId)) throw new RotationException("invalid_item");
        var user = users.GetUserById(userId) ?? throw new RotationException("permission_denied");
        var config = Plugin.Instance?.Configuration;
        if (config?.Enabled != true || user.HasPermission(PermissionKind.IsDisabled)
            || (!user.HasPermission(PermissionKind.IsAdministrator) && !config.AllowedUserIds.Any(id => Guid.TryParse(id, out var allowed) && allowed == userId))) throw new RotationException("permission_denied");
        var item = library.GetItemById<Video>(id, userId) ?? throw new RotationException("item_missing");
        var sources = media.GetStaticMediaSources(item, false, user);
        var source = sources.SingleOrDefault(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase)) ?? throw new RotationException("source_changed");
        if (source.Protocol != MediaProtocol.File || string.IsNullOrWhiteSpace(source.Path)) throw new RotationException("remote_source");
        if (Guid.TryParse(source.Id, out var versionId) && library.GetItemById<Video>(versionId, userId) is null) throw new RotationException("permission_denied");
        var path = Path.GetFullPath(source.Path);
        var roots = library.GetVirtualFolders().SelectMany(f => f.Locations ?? []).Where(root => IsChild(root, path)).OrderByDescending(s => s.Length).ToArray();
        if (roots.Length == 0) throw new RotationException("outside_library");
        var configured = Plugin.Instance?.Configuration.AllowedRoots ?? [];
        if (configured.Length > 0 && !configured.Any(root => IsChild(root, path))) throw new RotationException("outside_library");
        var root = roots[0]; _ = RotationFiles.Validate(path, root);
        return new(userId, item, source.Id, path, root);
    }
    public ResolvedVideo ResolvePrimary(Guid userId, string itemId, string? sourceId)
    {
        if (!string.IsNullOrEmpty(sourceId)) return Resolve(userId, itemId, sourceId);
        if (!Guid.TryParse(itemId, out var id)) throw new RotationException("invalid_item");
        var user = users.GetUserById(userId) ?? throw new RotationException("permission_denied");
        var item = library.GetItemById<Video>(id, userId) ?? throw new RotationException("item_missing");
        var sources = media.GetStaticMediaSources(item, false, user).Where(s => s.Protocol == MediaProtocol.File && s.Path == item.Path).ToArray();
        if (sources.Length != 1) throw new RotationException("version_selection_required");
        return Resolve(userId, itemId, sources[0].Id);
    }
    public bool HasPlayback(ResolvedVideo video) => sessions.Sessions.Any(s =>
        s.NowPlayingItem is not null && (s.NowPlayingItem.Id == video.Item.Id || string.Equals(s.NowPlayingItem.Path, video.Path, StringComparison.Ordinal)));
    public void Refresh(ResolvedVideo video)
    {
        // Queue a fresh probe and derived images without replacing curated text metadata.
        var options = new MetadataRefreshOptions(new DirectoryService(filesystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllMetadata = false, ReplaceAllImages = false, ForceSave = true, IsAutomated = false, RegenerateTrickplay = true
        };
        providers.QueueRefresh(video.Item.Id, options, RefreshPriority.High);
    }
    public void Revalidate(RotationJob job)
    {
        if (Plugin.Instance?.Configuration.Enabled != true) throw new RotationException("plugin_disabled");
        var resolved = Resolve(job.UserId, job.Request.ItemId, job.Request.MediaSourceId);
        if (resolved.Path != job.Path || resolved.Root != job.Root) throw new RotationException("source_changed");
    }
    public void RequireAdministrator(System.Security.Claims.ClaimsPrincipal principal)
    {
        var user = users.GetUserById(UserId(principal));
        if (user is null || user.HasPermission(PermissionKind.IsDisabled) || !user.HasPermission(PermissionKind.IsAdministrator)) throw new RotationException("permission_denied");
    }
    public void ValidateRecovery(RotationJob job)
    {
        // Recovery is independent of an indexed Video and remains available
        // when normal writes are disabled. Current library/root limits still apply.
        var roots = library.GetVirtualFolders().SelectMany(f => f.Locations ?? []);
        if (!roots.Any(root => IsChild(root, job.Path))) throw new RotationException("outside_library");
        var configured = Plugin.Instance?.Configuration.AllowedRoots ?? [];
        if (configured.Length > 0 && !configured.Any(root => IsChild(root, job.Path))) throw new RotationException("outside_library");
        _ = RotationFiles.Validate(job.Path, job.Root, allowMissing: true);
    }
    private static bool IsChild(string root, string file) => file.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
