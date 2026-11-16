using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Jellyfin.Plugin.GlideToolkit.Core;
namespace Jellyfin.Plugin.GlideToolkit;

[ApiController, Authorize, Route("GlideToolkit/FileManagement")]
public sealed class FileManagementController(FileManagementLibrary library, FileManagementJobs jobs) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [HttpGet("Capabilities")]
    public IActionResult Capabilities() => new JsonResult(new { protocolVersion = 1, pluginVersion = "1.2.0.0", available = library.Allowed(RotationLibrary.UserId(User)), ready = jobs.Ready, maximumItems = 500, softDelete = true, sameLibraryOnly = true }, Json);
    public sealed record InspectRequest(string[] ItemIds);
    [HttpPost("Inspect"), RequestSizeLimit(64 * 1024)]
    public IActionResult Inspect([FromBody] InspectRequest request) => Guard(user => jobs.Inspect(user, request.ItemIds));
    [HttpGet("Folders")]
    public IActionResult Folders([FromQuery] string? parent = null, [FromQuery] int start = 0) => Guard(user => library.Folders(user, parent, start));
    [HttpGet("Batches")]
    public IActionResult List() => Guard(jobs.List);
    [HttpPost("Batches"), RequestSizeLimit(512 * 1024)]
    public IActionResult Submit([FromBody] FileActionRequest request) => Guard(user => jobs.Submit(user, request), 202);
    [HttpGet("Batches/{id:guid}")]
    public IActionResult Detail(Guid id) => Guard(user => jobs.Get(user, id));
    [HttpPost("Batches/{id:guid}/{command}")]
    public IActionResult Change(Guid id, string command) => Guard(user => jobs.Change(user, id, command));
    private IActionResult Guard(Func<Guid, object> action, int status = 200)
    {
        try { return new JsonResult(action(RotationLibrary.UserId(User)), Json) { StatusCode = status }; }
        catch (RotationException e) { return Failure(e.Code, e.Code is "permission_denied" or "file_management_disabled" ? 403 : 409); }
        catch (UnauthorizedAccessException) { return Failure("write_denied", 403); }
        catch (Exception) { return Failure("file_unavailable", 409); }
    }
    private static JsonResult Failure(string code, int status) => new(new { errorCode = code }, Json) { StatusCode = status };
}
