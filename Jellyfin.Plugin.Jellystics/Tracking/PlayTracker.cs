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
    private DateTimeOffset _lastPosAt;

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
        _lastPosAt = start.At;
        TrackStreams(start);
        Dirty = true;
    }

    public PlayRecord Record { get; }
    public bool Dirty { get; private set; }
    public bool IsPlaying { get; private set; }

    public void Apply(PlayEvent e)
    {
        if (Record.State == PlayStates.Closed) return;
        var at = e.At < _lastAt ? _lastAt : e.At;   // never go backwards
        var dt = (long)(at - _lastAt).TotalMilliseconds;
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
            // Expected advance since the last known position: real time while playing, nothing while paused.
            var expected = IsPlaying ? (long)(at - _lastPosAt).TotalMilliseconds : 0;
            if (d > expected + SeekThresholdMs) { Record.SeekCount++; Record.SkippedMs += d - expected; }
            else if (d < -SeekThresholdMs) { Record.SeekCount++; }
        }

        // Clients often pause right before stopping: that is not a user pause.
        if (IsPlaying && e.IsPaused && e.Kind != PlayEventKind.Stop) Record.PauseCount++;
        IsPlaying = !e.IsPaused;
        // Jellyfin clears the play state indexes (RemoveNowPlayingItem) before raising PlaybackStopped: a Stop carries no stream info.
        if (e.Kind != PlayEventKind.Stop) TrackStreams(e with { At = at });

        if (e.PositionTicks is not null) { Record.PositionEndTicks = e.PositionTicks; _lastPos = e.PositionTicks; _lastPosAt = at; }
        Record.LastEventAt = at;
        _lastAt = at;
        Record.WatchedMs = Math.Max(0, Math.Min(Record.WatchedMs, (long)(at - Record.StartedAt).TotalMilliseconds));
        Dirty = true;

        if (e.Kind == PlayEventKind.Stop)
        {
            Record.PlayedToCompletion = e.PlayedToCompletion;
            CloseAt(at);
        }
    }

    /// <summary>Closes the play at <paramref name="at"/> without crediting time after the last event.</summary>
    public void CloseAt(DateTimeOffset at)
    {
        if (Record.State == PlayStates.Closed) return;
        if (at < Record.LastEventAt) at = Record.LastEventAt;
        Record.EndedAt = at < Record.StartedAt ? Record.StartedAt : at;
        Record.State = PlayStates.Closed;
        Record.WatchedMs = Math.Max(0, Math.Min(Record.WatchedMs, (long)(Record.EndedAt.Value - Record.StartedAt).TotalMilliseconds));
        if (_audioMs.Count > 0)
        {
            var dominant = _audioMs.MaxBy(kv => kv.Value).Key;
            Record.AudioStreamIndex = dominant;
            Record.AudioLanguage = _audioLang.GetValueOrDefault(dominant);
        }
        Record.StreamChanges = JsonSerializer.Serialize(_streamChanges);
        Dirty = true;
    }

    /// <summary>Marks the record as changed externally (e.g. transcoding info refreshed).</summary>
    public void Touch() => Dirty = true;

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
