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
        var deleted = 0;
        _queue.Enqueue(() => deleted = _store.PurgeBefore(request.Before));
        await _queue.FlushAsync().ConfigureAwait(false);
        return Ok(new { deleted });
    }
}
