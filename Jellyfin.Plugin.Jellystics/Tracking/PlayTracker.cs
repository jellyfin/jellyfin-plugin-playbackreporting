using System.Text.Json;
using Jellyfin.Plugin.Jellystics.Models;

namespace Jellyfin.Plugin.Jellystics.Tracking;

/// <summary>Pure state machine for one play (spec §4.2). No Jellyfin dependency.</summary>
public sealed class PlayTracker
{
    public const long PositionSlackMs = 2000;
    public const long SeekThresholdMs = 5000;
    private const long TicksPerMs = 10_000;

    private readonly List<object> _streamChanges = new();
    private readonly Dictionary<int, long> _audioMs = new();
    private readonly Dictionary<int, string?> _audioLang = new();
    private DateTimeOffset _lastAt;
    private long? _lastPos;

    public PlayTracker(PlayRecord record, PlayEvent start)
    {
        Record = record;
        Record.State = PlayStates.Open;
        Record.StartedAt = start.At;
        Record.LastEventAt = start.At;
        Record.PositionStartTicks = start.PositionTicks;
        Record.PositionEndTicks = start.PositionTicks;
        IsPlaying = !start.IsPaused;
        _lastAt = start.At;
        _lastPos = start.PositionTicks;
        TrackStreams(start);
        Dirty = true;
    }

    public PlayRecord Record { get; }
    public bool Dirty { get; private set; }
    public bool IsPlaying { get; private set; }

    public void Apply(PlayEvent e)
    {
        if (Record.State == PlayStates.Closed) return;
        var dt = Math.Max(0, (long)(e.At - _lastAt).TotalMilliseconds);
        long? dp = e.PositionTicks is { } p && _lastPos is { } lp ? (p - lp) / TicksPerMs : null;

        if (IsPlaying)
        {
            var credit = dp is null ? dt : Math.Min(dt, Math.Max(dp.Value, 0) + PositionSlackMs);
            Record.WatchedMs += credit;
            if (Record.AudioStreamIndex is { } a) _audioMs[a] = _audioMs.GetValueOrDefault(a) + credit;
        }
        else
        {
            Record.PausedMs += dt;
        }

        if (dp is { } d && !e.IsAutomated)
        {
            if (d > dt + SeekThresholdMs) { Record.SeekCount++; Record.SkippedMs += d - dt; }
            else if (d < -SeekThresholdMs) { Record.SeekCount++; }
        }

        if (IsPlaying && e.IsPaused) Record.PauseCount++;
        IsPlaying = !e.IsPaused;
        TrackStreams(e);

        if (e.PositionTicks is not null) { Record.PositionEndTicks = e.PositionTicks; _lastPos = e.PositionTicks; }
        Record.LastEventAt = e.At;
        _lastAt = e.At;
        Record.WatchedMs = Math.Min(Record.WatchedMs, (long)(e.At - Record.StartedAt).TotalMilliseconds);
        Dirty = true;

        if (e.Kind == PlayEventKind.Stop)
        {
            Record.PlayedToCompletion = e.PlayedToCompletion;
            CloseAt(e.At);
        }
    }

    /// <summary>Closes the play at <paramref name="at"/> without crediting time after the last event.</summary>
    public void CloseAt(DateTimeOffset at)
    {
        if (Record.State == PlayStates.Closed) return;
        Record.EndedAt = at < Record.StartedAt ? Record.StartedAt : at;
        Record.State = PlayStates.Closed;
        if (_audioMs.Count > 0)
        {
            var dominant = _audioMs.MaxBy(kv => kv.Value).Key;
            Record.AudioStreamIndex = dominant;
            Record.AudioLanguage = _audioLang.GetValueOrDefault(dominant) ?? Record.AudioLanguage;
        }
        Record.StreamChanges = JsonSerializer.Serialize(_streamChanges);
        Dirty = true;
    }

    public PlayRecord Snapshot()
    {
        if (Record.State == PlayStates.Open) Record.StreamChanges = JsonSerializer.Serialize(_streamChanges);
        Dirty = false;
        return Record.Clone();
    }

    private void TrackStreams(PlayEvent e)
    {
        if (e.AudioStreamIndex is { } a && a != Record.AudioStreamIndex)
        {
            if (Record.AudioStreamIndex is not null) _streamChanges.Add(new { at = e.At.UtcDateTime.ToString("O"), kind = "audio", index = a, language = e.AudioLanguage });
            Record.AudioStreamIndex = a;
            Record.AudioLanguage = e.AudioLanguage;
            _audioLang[a] = e.AudioLanguage;
        }
        if (e.SubtitleStreamIndex != Record.SubtitleStreamIndex && (e.SubtitleStreamIndex is not null || Record.SubtitleStreamIndex is not null) && e.Kind != PlayEventKind.Start)
        {
            _streamChanges.Add(new { at = e.At.UtcDateTime.ToString("O"), kind = "subtitle", index = e.SubtitleStreamIndex, language = e.SubtitleLanguage });
        }
        if (e.Kind == PlayEventKind.Start || e.SubtitleStreamIndex != Record.SubtitleStreamIndex)
        {
            Record.SubtitleStreamIndex = e.SubtitleStreamIndex;
            Record.SubtitleLanguage = e.SubtitleLanguage;
        }
    }
}
