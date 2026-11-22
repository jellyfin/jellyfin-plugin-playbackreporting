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
}
