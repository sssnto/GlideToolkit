using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.GlideToolkit.Core;

namespace Jellyfin.Plugin.GlideToolkit;

public sealed class RotationBatchDispatcher(RotationBatches batches, RotationJobs jobs, RotationLibrary library,
    ILogger<RotationBatchDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A damaged ledger fails closed, never reconstructing write operations from UI state.
        try { batches.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { logger.LogWarning("Glide batch journal unavailable; batch dispatch disabled until repaired"); return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            if (jobs.Ready && batches.Ready)
            {
                try
                {
                    batches.Tick((user, entry) =>
                    {
                        try
                        {
                            var video = library.ResolvePrimary(user, entry.ItemId, entry.MediaSourceId);
                            return new BatchPrepared(entry.ItemId, video.SourceId, RotationFiles.Version(video.Path), video.Item.Name);
                        }
                        catch (IOException) { throw new RotationException("file_unavailable"); }
                        catch (UnauthorizedAccessException) { throw new RotationException("write_denied"); }
                    }, jobs.FindByRequest, jobs.Find, (user, request) => jobs.Submit(library.Resolve(user, request.ItemId, request.MediaSourceId), request));
                }
                catch (IOException) { logger.LogWarning("Glide batch journal IO failed; dispatch deferred without changing request identity"); }
                catch (UnauthorizedAccessException) { logger.LogWarning("Glide batch journal access failed; dispatch deferred"); }
            }
            await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
        }
    }
}
