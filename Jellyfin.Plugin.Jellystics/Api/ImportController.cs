using Jellyfin.Plugin.Jellystics.Import;
using Jellyfin.Plugin.Jellystics.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Jellystics.Api;

public sealed record PurgeRequest(DateTimeOffset Before);

/// <summary>Admin endpoints: import the official Playback Reporting history, purge old plays.</summary>
[ApiController]
[Route("Jellystics")]
[Authorize(Policy = "RequiresElevation")]
public class ImportController : ControllerBase
{
    private readonly ImportJobs _jobs;
    private readonly PlayStore _store;
    private readonly WriteQueue _queue;

    public ImportController(ImportJobs jobs, PlayStore store, WriteQueue queue)
    {
        _jobs = jobs; _store = store; _queue = queue;
    }

    [HttpPost("Import/PlaybackReporting")]
    public ActionResult StartImport()
    {
        var plugin = Plugin.Instance!;
        var configured = plugin.Configuration.PbrDbPath;
        var path = string.IsNullOrWhiteSpace(configured) ? Path.Combine(plugin.AppDataPath, "playback_reporting.db") : configured;
        if (!System.IO.File.Exists(path)) return NotFound(new { error = $"File not found: {path}" });
        if (!_jobs.TryStartPbr(path)) return Conflict(new { error = "An import is already running" });
        return Accepted(new { started = true });
    }

    [HttpGet("Import/Status")]
    public ActionResult<ImportStatus> GetStatus() => _jobs.Status;

    [HttpPost("Purge")]
    public async Task<ActionResult> Purge([FromBody] PurgeRequest request)
    {
        if (request is null || request.Before == default) return BadRequest(new { error = "A \"before\" date is required" });
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _queue.Enqueue(() =>
        {
            try { done.SetResult(_store.PurgeBefore(request.Before)); }
            catch (Exception ex) { done.SetException(ex); }
        });
        if (!queued) return StatusCode(503, new { error = "Jellyfin is shutting down" });
        try { return Ok(new { deleted = await done.Task.ConfigureAwait(false) }); }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }
}
