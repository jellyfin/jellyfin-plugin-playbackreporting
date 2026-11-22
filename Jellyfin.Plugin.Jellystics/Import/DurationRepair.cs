namespace Jellyfin.Plugin.Jellystics.Import;

/// <summary>Repairs impossible imported durations (same rules as Jellystics).</summary>
public static class DurationRepair
{
    public const long MaxUnknownSeconds = 12 * 3600;

    public static long RepairSeconds(long seconds, long? runTimeTicks)
    {
        if (seconds < 0) return 0;
        if (runTimeTicks is > 0)
        {
            var runtime = (long)Math.Ceiling(runTimeTicks.Value / 10_000_000.0);
            return seconds > runtime * 1.5 ? runtime : seconds;
        }
        return Math.Min(seconds, MaxUnknownSeconds);
    }
}
