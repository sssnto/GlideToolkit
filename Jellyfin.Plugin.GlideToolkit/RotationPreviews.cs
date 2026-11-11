using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Jellyfin.Plugin.GlideToolkit.Core;

namespace Jellyfin.Plugin.GlideToolkit;

/// On-demand frames for saved files, separate from curated movie/series posters.
/// At most one decoder, cached on the server, no media transferred to the phone.
public sealed class RotationPreviews(IApplicationPaths paths, IMediaEncoder encoder, IMediaSourceManager media)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<string> Image(ResolvedVideo video, string revision, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var version = RotationFiles.Version(video.Path);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(video.Path + revision + version)));
            var directory = Path.Combine(paths.CachePath, "glide-rotation-previews");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, key + ".jpg");
            if (File.Exists(file)) return file;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var sources = media.GetStaticMediaSources(video.Item, false);
            var source = sources.SingleOrDefault(s => s.Id.Equals(video.SourceId, StringComparison.OrdinalIgnoreCase)) ?? throw new RotationException("source_changed");
            var stream = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video) ?? throw new RotationException("invalid_container");
            // FFmpeg reads the current file's display matrix, including its saved rotation.
            var extracted = await encoder.ExtractVideoImage(video.Path, source.Container, source, stream,
                video.Item.Video3DFormat, TimeSpan.FromSeconds(1), timeout.Token).ConfigureAwait(false);
            try
            {
                if (RotationFiles.Version(video.Path) != version) throw new RotationException("file_changed");
                File.Copy(extracted, file, true);
            }
            finally { try { File.Delete(extracted); } catch (IOException) { } }
            // Bounded derived cache; backups and media are never touched.
            long bytes = 0; var count = 0;
            foreach (var frame in Directory.EnumerateFiles(directory, "*.jpg").Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTimeUtc))
            {
                bytes += frame.Length; count++;
                if (frame.FullName != file && (count > 1000 || bytes > 256L * 1024 * 1024))
                    try { frame.Delete(); } catch (IOException) { }
            }
            return file;
        }
        finally { _gate.Release(); }
    }
}
