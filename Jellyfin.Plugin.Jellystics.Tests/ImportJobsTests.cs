using Jellyfin.Plugin.Jellystics.Import;
using Jellyfin.Plugin.Jellystics.Storage;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class ImportJobsTests
{
    private static async Task<ImportStatus> WaitFor(ImportJobs jobs, string state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (jobs.Status.State == state) return jobs.Status;
            await Task.Delay(10);
        }
        throw new TimeoutException($"state stayed {jobs.Status.State}");
    }

    [Fact]
    public async Task ImportsARealPbrDbAndReportsDone()
    {
        using var t = new TempDb();
        var store = new PlayStore(t.Db);
        var importer = new PbrImporter(store, new NoItems(), TimeZoneInfo.Utc);
        var jobs = new ImportJobs(path => importer.Import(path));
        var pbr = PbrImporterTests.MakePbrDb(("2026-08-01 21:00:20.0000000", "u2", "m9", "Movie", "Atlas", "DirectPlay", 5400));

        Assert.Equal("idle", jobs.Status.State);
        Assert.True(jobs.TryStartPbr(pbr));
        var s = await WaitFor(jobs, "done");
        Assert.Equal(1, s.Imported);
        Assert.NotNull(s.FinishedAt);
        Assert.Null(s.Error);
    }

    [Fact]
    public async Task RejectsSecondStartWhileRunning()
    {
        var gate = new TaskCompletionSource();
        var jobs = new ImportJobs(_ => { gate.Task.Wait(); return new PbrImportResult(3, 2, 1, 0); });

        Assert.True(jobs.TryStartPbr("x"));
        Assert.Equal("running", jobs.Status.State);
        Assert.False(jobs.TryStartPbr("x"));
        gate.SetResult();
        var s = await WaitFor(jobs, "done");
        Assert.Equal((3, 2, 1), (s.Read, s.Imported, s.Skipped));
        Assert.True(jobs.TryStartPbr("x"));
    }

    [Fact]
    public async Task ReportsFailure()
    {
        var jobs = new ImportJobs(_ => throw new InvalidOperationException("boom"));
        Assert.True(jobs.TryStartPbr("x"));
        var s = await WaitFor(jobs, "failed");
        Assert.Contains("boom", s.Error);
    }

    private sealed class NoItems : IItemLookup
    {
        public ItemInfo? Find(string itemId) => null;
    }
}
