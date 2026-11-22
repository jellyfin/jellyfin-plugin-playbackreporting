using Jellyfin.Plugin.Jellystics.Api;
using Jellyfin.Plugin.Jellystics.Import;
using Jellyfin.Plugin.Jellystics.Models;
using Jellyfin.Plugin.Jellystics.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class ImportControllerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayRecord Play(string id, DateTimeOffset at, string state) =>
        new() { Id = id, UserId = "u", ItemId = "i", StartedAt = at, LastEventAt = at, State = state };

    private static async Task<(ImportController, PlayStore, WriteQueue)> Make(TempDb t)
    {
        var q = new WriteQueue(NullLogger<WriteQueue>.Instance);
        await q.StartAsync(CancellationToken.None);
        var store = new PlayStore(t.Db);
        return (new ImportController(new ImportJobs((_, _) => new PbrImportResult(0, 0, 0, 0)), store, q), store, q);
    }

    [Fact]
    public async Task PurgeDeletesOnlyClosedPlaysBeforeTheDate()
    {
        using var t = new TempDb();
        var (c, store, q) = await Make(t);
        store.Upsert(new[] { Play("old-closed", T0, PlayStates.Closed), Play("old-open", T0, PlayStates.Open), Play("new", T0.AddYears(1), PlayStates.Closed) });
        var res = await c.Purge(new PurgeRequest(T0.AddMonths(1)));
        Assert.Equal(200, Assert.IsType<OkObjectResult>(res).StatusCode);
        Assert.Equal(new[] { "new", "old-open" }, store.Since(0, 10).Select(p => p.Id).Order());
        await q.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PurgeRejectsMissingOrDefaultDate()
    {
        using var t = new TempDb();
        var (c, _, q) = await Make(t);
        Assert.IsType<BadRequestObjectResult>(await c.Purge(null!));
        Assert.IsType<BadRequestObjectResult>(await c.Purge(new PurgeRequest(default)));
        await q.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PurgeReportsFailuresAs500()
    {
        var t = new TempDb();
        var (c, _, q) = await Make(t);
        t.Dispose();   // db file gone: the Plays table no longer exists
        var res = await c.Purge(new PurgeRequest(T0));
        Assert.Equal(500, Assert.IsType<ObjectResult>(res).StatusCode);
        await q.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PurgeWhileShuttingDownIs503AndDoesNotHang()
    {
        using var t = new TempDb();
        var (c, _, q) = await Make(t);
        await q.StopAsync(CancellationToken.None);
        var res = await c.Purge(new PurgeRequest(T0)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(503, Assert.IsType<ObjectResult>(res).StatusCode);
    }
}
