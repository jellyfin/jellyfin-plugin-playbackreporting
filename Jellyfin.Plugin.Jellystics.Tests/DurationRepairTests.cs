using Jellyfin.Plugin.Jellystics.Import;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class DurationRepairTests
{
    private const long Min45 = 45L * 60 * 10_000_000;

    [Theory]
    [InlineData(-2147483648L, Min45, 0L)]
    [InlineData(-1213L, null, 0L)]
    [InlineData(166552L, Min45, 2700L)]   // > 1.5 × runtime → runtime
    [InlineData(3000L, Min45, 3000L)]     // < 1.5 × runtime → kept
    [InlineData(100000L, null, 43200L)]   // unknown runtime → 12 h cap
    [InlineData(1200L, null, 1200L)]
    public void Repairs(long input, long? runtime, long expected) => Assert.Equal(expected, DurationRepair.RepairSeconds(input, runtime));
}
