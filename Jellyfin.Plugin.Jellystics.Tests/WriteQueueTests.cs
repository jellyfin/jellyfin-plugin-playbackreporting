using Jellyfin.Plugin.Jellystics.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class WriteQueueTests
{
    [Fact]
    public async Task RunsWorkSequentiallyAndSurvivesFailures()
    {
        var q = new WriteQueue(NullLogger<WriteQueue>.Instance);
        await q.StartAsync(CancellationToken.None);
        var seen = new List<int>();
        q.Enqueue(() => seen.Add(1));
        q.Enqueue(() => throw new InvalidOperationException("boom"));
        q.Enqueue(() => seen.Add(2));
        await q.FlushAsync();
        Assert.Equal(new[] { 1, 2 }, seen);
        await q.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopDrainsPendingWorkInOrder()
    {
        var q = new WriteQueue(NullLogger<WriteQueue>.Instance);
        await q.StartAsync(CancellationToken.None);
        var seen = new List<int>();
        for (var i = 0; i < 200; i++) { var n = i; q.Enqueue(() => { if (n == 0) Thread.Sleep(50); seen.Add(n); }); }
        await q.StopAsync(CancellationToken.None);
        Assert.Equal(Enumerable.Range(0, 200), seen);
    }

    [Fact]
    public async Task EnqueueAfterStopIsRejectedAndFlushDoesNotHang()
    {
        var q = new WriteQueue(NullLogger<WriteQueue>.Instance);
        await q.StartAsync(CancellationToken.None);
        await q.StopAsync(CancellationToken.None);
        Assert.False(q.Enqueue(() => { }));
        Assert.True(q.FlushAsync().IsCompleted);
    }

    [Fact]
    public async Task AFailedActionIsRetriedOnce()
    {
        var q = new WriteQueue(NullLogger<WriteQueue>.Instance);
        await q.StartAsync(CancellationToken.None);
        var attempts = 0;
        q.Enqueue(() => { attempts++; if (attempts == 1) throw new InvalidOperationException("busy"); });
        await q.FlushAsync();
        Assert.Equal(2, attempts);
        await q.StopAsync(CancellationToken.None);
    }
}
