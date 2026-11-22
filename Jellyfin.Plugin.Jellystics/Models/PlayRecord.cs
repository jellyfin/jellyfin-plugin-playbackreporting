namespace Jellyfin.Plugin.Jellystics.Models;

/// <summary>One playback (spec §3.1). Times are UTC.</summary>
public sealed class PlayRecord
{
    public string Id { get; set; } = string.Empty;
    public string? PlaySessionId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public string? ItemName { get; set; }
    public string? ItemType { get; set; }
    public string? SeriesId { get; set; }
    public string? SeriesName { get; set; }
    public string? SeasonId { get; set; }
    public string? AlbumId { get; set; }
    public string? AlbumName { get; set; }
    public int? ParentIndexNumber { get; set; }
    public int? IndexNumber { get; set; }
    public string? MediaSourceId { get; set; }
    public string? LibraryId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public long WatchedMs { get; set; }
    public long PausedMs { get; set; }
    public int PauseCount { get; set; }
    public int SeekCount { get; set; }
    public long SkippedMs { get; set; }
    public long? PositionStartTicks { get; set; }
    public long? PositionEndTicks { get; set; }
    public long? RunTimeTicks { get; set; }
    public bool? PlayedToCompletion { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? Client { get; set; }
    public string? ApplicationVersion { get; set; }
    public string? RemoteEndPoint { get; set; }
    public bool? IsRemote { get; set; }
    public string? PlayMethod { get; set; }
    public int? AudioStreamIndex { get; set; }
    public string? AudioLanguage { get; set; }
    public string? AudioCodec { get; set; }
    public int? SubtitleStreamIndex { get; set; }
    public string? SubtitleLanguage { get; set; }
    public string StreamChanges { get; set; } = "[]";
    public string? VideoCodecFrom { get; set; }
    public string? VideoCodecTo { get; set; }
    public string? AudioCodecFrom { get; set; }
    public string? AudioCodecTo { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Bitrate { get; set; }
    public float? Framerate { get; set; }
    public string? HwAccel { get; set; }
    public string? TranscodeReasons { get; set; }
    public string? VideoRange { get; set; }
    public bool? IsVideoDirect { get; set; }
    public bool? IsAudioDirect { get; set; }
    public int? SourceWidth { get; set; }
    public int? SourceHeight { get; set; }
    public string? VideoRangeType { get; set; }
    public string State { get; set; } = PlayStates.Open;
    public DateTimeOffset LastEventAt { get; set; }
    public string Source { get; set; } = PlaySources.Plugin;
    public bool? DurationRepaired { get; set; }   // import-pbr only: DurationRepair changed the duration (null for live plays)
    public long Version { get; set; }

    public PlayRecord Clone() => (PlayRecord)MemberwiseClone();
}

public static class PlayStates { public const string Open = "open"; public const string Closed = "closed"; }
public static class PlaySources { public const string Plugin = "plugin"; public const string ImportPbr = "import-pbr"; public const string ImportJellystics = "import-jellystics"; }
