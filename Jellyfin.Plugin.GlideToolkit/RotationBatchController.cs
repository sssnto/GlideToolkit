using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Jellyfin.Plugin.GlideToolkit.Core;

namespace Jellyfin.Plugin.GlideToolkit;

[ApiController, Authorize, Route("GlideToolkit")]
public sealed class RotationBatchController(RotationLibrary library, RotationJobs jobs, RotationBatches batches, RotationPreviews previews) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [HttpPost("Batches"), RequestSizeLimit(4 * 1024 * 1024)]
    public IActionResult Submit([FromBody] BatchRequest request) => Guard(user => batches.Submit(user, request), 202);
    [HttpGet("Batches")]
    public IActionResult List() => Guard(user => batches.List(user, jobs.Find));
    [HttpGet("Batches/{id:guid}")]
    public IActionResult Detail(Guid id, [FromQuery] bool includeItems = true) => Guard(user => batches.Get(user, id, jobs.Find, includeItems));
    [HttpPost("Batches/{id:guid}/{command}")]
    public IActionResult Change(Guid id, string command) => Guard(user => batches.Change(user, id, command, jobs.Find, jobs.FindByRequest));
    public sealed record ItemStateRequest(string[] ItemIds);
    [HttpPost("Items/RotationStates"), RequestSizeLimit(64 * 1024)]
    public IActionResult States([FromBody] ItemStateRequest request) => Guard(user =>
    {
        if (request.ItemIds is null || request.ItemIds.Length > 1000 || request.ItemIds.Any(s => !Guid.TryParse(s, out _))) throw new RotationException("invalid_request");
        var ids = request.ItemIds.Select(RotationLibrary.ItemKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var states = jobs.ItemStates(user, ids).ToDictionary(s => s.ItemId, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in batches.ItemStates(user, ids))
        {
            states.TryGetValue(entry.ItemId, out var saved);
            states[entry.ItemId] = new RotationJobs.ItemRotationState(entry.ItemId, entry.MediaSourceId ?? "", entry.State, entry.Stage ?? entry.State,
                entry.Progress, entry.ErrorCode, saved?.Revision, saved?.Restored ?? false, entry.JobId, saved?.PreviewMediaSourceId);
        }
        return states.Values.ToArray();
    });
    [HttpGet("Items/{itemId}/RotationPreview")]
    public async Task<IActionResult> Preview(string itemId, [FromQuery] string revision, CancellationToken token)
    {
        try
        {
            if (!library.Allowed(User)) throw new RotationException("permission_denied");
            if (revision is null || revision.Length != 64) throw new RotationException("invalid_request");
            var video = jobs.ResolvePreview(RotationLibrary.UserId(User), itemId, revision);
            var file = await previews.Image(video, revision, token).ConfigureAwait(false);
            Response.Headers.CacheControl = "private, max-age=604800";
            return PhysicalFile(file, "image/jpeg");
        }
        catch (RotationException error) { return Failure(error.Code, 409); }
        catch (IOException) { return Failure("preview_failed", 409); }
        catch (UnauthorizedAccessException) { return Failure("permission_denied", 403); }
        catch (OperationCanceledException) { return Failure("preview_timeout", 408); }
        catch (Exception) { return Failure("preview_failed", 409); }
    }
    private IActionResult Guard<T>(Func<Guid, T> action, int success = 200)
    {
        try
        {
            if (!library.Allowed(User)) throw new RotationException("permission_denied");
            if (!jobs.Ready) throw new RotationException("server_preparing");
            if (!batches.Ready) throw new RotationException("journal_unavailable");
            if (!OperatingSystem.IsLinux()) throw new RotationException("platform_unsupported");
            return new JsonResult(action(RotationLibrary.UserId(User)), Json) { StatusCode = success };
        }
        catch (RotationException error) { return Failure(error.Code, error.Code == "permission_denied" ? 403 : error.Code == "job_missing" ? 404 : 409); }
        catch (IOException) { return Failure("journal_unavailable", 503); }
        catch (UnauthorizedAccessException) { return Failure("write_denied", 403); }
    }
    private static JsonResult Failure(string code, int status) => new(new { errorCode = code }, Json) { StatusCode = status };
}
