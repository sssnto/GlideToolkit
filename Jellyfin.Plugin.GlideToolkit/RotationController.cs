using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Jellyfin.Plugin.GlideToolkit.Core;

namespace Jellyfin.Plugin.GlideToolkit;

[ApiController]
[Route("GlideToolkit")]
[Authorize]
public sealed class RotationController(RotationLibrary library, RotationJobs jobs) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [HttpGet("Capabilities")]
    public IActionResult Capabilities() => Guard(() => new RotationCapability(1, "1.1.0", library.Allowed(User) && jobs.Ready && OperatingSystem.IsLinux(),
        !library.Allowed(User) ? "permission_denied" : !jobs.Ready ? "server_preparing" : !OperatingSystem.IsLinux() ? "platform_unsupported" : null,
        SupportedExtensions: ["mp4", "mov", "m4v"]));

    [HttpGet("Items/{itemId}/Rotation")]
    public IActionResult Inspect(string itemId, [FromQuery] string mediaSourceId) => Guard(() =>
    {
        if (!jobs.Ready) throw new RotationException("server_preparing");
        var video = library.Resolve(User, itemId, mediaSourceId);
        return new RotationCapability(1, "1.1.0", true, null, itemId, video.SourceId, Path.GetFileName(video.Path),
            RotationFiles.Version(video.Path), new FileInfo(video.Path).Length, ["mp4", "mov", "m4v"]);
    });

    [HttpPost("Jobs")]
    public IActionResult Submit([FromBody] RotationRequest request) => Guard(() =>
    {
        if (request.RequestId == Guid.Empty || request.QuarterTurns is < 0 or > 3 || (request.RestoreJobId is null && request.QuarterTurns == 0)
            || (request.RestoreJobId is not null && request.QuarterTurns != 0)
            || string.IsNullOrWhiteSpace(request.ExpectedVersion) || request.ExpectedVersion.Length > 128) throw new RotationException("invalid_request");
        var video = library.Resolve(User, request.ItemId, request.MediaSourceId);
        return jobs.Submit(video, request);
    }, 202);

    [HttpGet("Jobs/{jobId:guid}")]
    public IActionResult Job(Guid jobId) => Guard(() =>
    {
        if (!library.Allowed(User)) throw new RotationException("permission_denied");
        return jobs.Find(RotationLibrary.UserId(User), jobId) ?? throw new RotationException("job_missing");
    });

    [HttpGet("Items/{itemId}/Jobs")]
    public IActionResult ItemJobs(string itemId, [FromQuery] string mediaSourceId) => Guard(() =>
    {
        if (!library.Allowed(User)) throw new RotationException("permission_denied");
        return jobs.ForItem(RotationLibrary.UserId(User), itemId, mediaSourceId);
    });

    [HttpPost("Jobs/{jobId:guid}/Verify")]
    public async Task<IActionResult> Verify(Guid jobId, CancellationToken token)
    {
        if (!library.Allowed(User)) return Forbid();
        try { return new JsonResult(await jobs.Verify(RotationLibrary.UserId(User), jobId, token).ConfigureAwait(false), Json); }
        catch (RotationException error) { return new JsonResult(new { errorCode = error.Code }, Json) { StatusCode = 409 }; }
    }

    [HttpGet("Recovery/Jobs")]
    public IActionResult RecoveryJobs() => Guard(() => { library.RequireAdministrator(User); return jobs.RecoveryJobs(); });

    [HttpGet("Jobs/{jobId:guid}/Recovery")]
    public Task<IActionResult> RecoveryInfo(Guid jobId, CancellationToken token) => Recover(jobId, false, token);

    public sealed record RecoveryConfirmation(bool Confirmed);
    [HttpPost("Jobs/{jobId:guid}/RecoverMissing")]
    public Task<IActionResult> RecoverMissing(Guid jobId, [FromBody] RecoveryConfirmation confirmation, CancellationToken token)
        => confirmation.Confirmed ? Recover(jobId, true, token)
            : Task.FromResult<IActionResult>(new JsonResult(new { errorCode = "confirmation_required" }, Json) { StatusCode = 409 });

    private async Task<IActionResult> Recover(Guid id, bool write, CancellationToken token)
    {
        try
        {
            library.RequireAdministrator(User);
            return new JsonResult(await jobs.Recovery(id, write, token).ConfigureAwait(false), Json);
        }
        catch (RotationException error) { return new JsonResult(new { errorCode = error.Code }, Json) { StatusCode = error.Code == "permission_denied" ? 403 : 409 }; }
        catch (IOException) { return new JsonResult(new { errorCode = "recovery_io_failed" }, Json) { StatusCode = 409 }; }
        catch (UnauthorizedAccessException) { return new JsonResult(new { errorCode = "write_denied" }, Json) { StatusCode = 403 }; }
    }

    private IActionResult Guard<T>(Func<T> action, int success = 200)
    {
        try { return new JsonResult(action(), Json) { StatusCode = success }; }
        catch (RotationException error)
        {
            var status = error.Code switch
            {
                "permission_denied" => 403,
                "item_missing" or "job_missing" => 404,
                "file_changed" or "request_conflict" or "restore_conflict" or "file_busy" or "source_changed" => 409,
                "server_preparing" or "queue_full" => 503,
                _ => 422
            };
            return new JsonResult(new { errorCode = error.Code }, Json) { StatusCode = status };
        }
        catch (UnauthorizedAccessException) { return new JsonResult(new { errorCode = "write_denied" }, Json) { StatusCode = 403 }; }
        catch (IOException) { return new JsonResult(new { errorCode = "io_failed" }, Json) { StatusCode = 409 }; }
    }
}
