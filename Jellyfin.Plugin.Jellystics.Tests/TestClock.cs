using Jellyfin.Plugin.Jellystics.Tracking;

namespace Jellyfin.Plugin.Jellystics.Tests;

internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 20, 19, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan d) => UtcNow += d;
}
