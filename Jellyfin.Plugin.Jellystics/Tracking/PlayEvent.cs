namespace Jellyfin.Plugin.Jellystics.Tracking;

public enum PlayEventKind { Start, Progress, Stop }

/// <summary>A Jellyfin playback event normalized for the tracker.</summary>
public sealed record PlayEvent(
    DateTimeOffset At, PlayEventKind Kind, long? PositionTicks, bool IsPaused, bool IsAutomated = false,
    int? AudioStreamIndex = null, string? AudioLanguage = null, int? SubtitleStreamIndex = null, string? SubtitleLanguage = null,
    bool? PlayedToCompletion = null);
