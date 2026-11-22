using Jellyfin.Plugin.Jellystics.Models;
using Jellyfin.Plugin.Jellystics.Tracking;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class PlayTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 19, 0, 0, TimeSpan.Zero);
    private const long TicksPerSecond = 10_000_000;
    private static long Pos(double seconds) => (long)(seconds * TicksPerSecond);
    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);
    private static PlayEvent Ev(double t, PlayEventKind k, double pos, bool paused = false, int? audio = null, bool? done = null) =>
        new(At(t), k, Pos(pos), paused, AudioStreamIndex: audio, PlayedToCompletion: done);
    private static PlayTracker Start(double pos = 0, bool paused = false) =>
        new(new PlayRecord { Id = "p", UserId = "u", ItemId = "i", RunTimeTicks = Pos(3600) }, Ev(0, PlayEventKind.Start, pos, paused));

    [Fact]
    public void SimplePlayCountsRealTime()
    {
        var t = Start();
        for (var s = 10; s <= 600; s += 10) t.Apply(Ev(s, PlayEventKind.Progress, s));
        t.Apply(Ev(605, PlayEventKind.Stop, 605, done: false));
        Assert.Equal(605_000, t.Record.WatchedMs);
        Assert.Equal(PlayStates.Closed, t.Record.State);
        Assert.Equal(At(605), t.Record.EndedAt);
        Assert.Equal(Pos(605), t.Record.PositionEndTicks);
        Assert.False(t.Record.PlayedToCompletion);
    }

    [Fact]
    public void PausesAreExcludedAndCounted()
    {
        var t = Start();
        t.Apply(Ev(60, PlayEventKind.Progress, 60));
        t.Apply(Ev(61, PlayEventKind.Progress, 61, paused: true));   // paused at 61
        t.Apply(Ev(361, PlayEventKind.Progress, 61, paused: true));  // 5 min paused
        t.Apply(Ev(362, PlayEventKind.Progress, 61));                // resumed
        t.Apply(Ev(422, PlayEventKind.Stop, 121));
        Assert.Equal(121_000, t.Record.WatchedMs);
        Assert.Equal(301_000, t.Record.PausedMs);
        Assert.Equal(1, t.Record.PauseCount);
    }

    [Fact]
    public void ForwardSeekIsCountedAndNotCreditedAsWatched()
    {
        var t = Start();
        t.Apply(Ev(10, PlayEventKind.Progress, 10));
        t.Apply(Ev(20, PlayEventKind.Progress, 110));  // skipped intro: +100 s of position in 10 s
        t.Apply(Ev(30, PlayEventKind.Progress, 120));
        Assert.Equal(1, t.Record.SeekCount);
        Assert.Equal(90_000, t.Record.SkippedMs);
        Assert.Equal(30_000, t.Record.WatchedMs);      // never more than real time
    }

    [Fact]
    public void BackwardSeekIsCountedAndPositionStallIsNotOverCredited()
    {
        var t = Start(pos: 600);
        t.Apply(Ev(10, PlayEventKind.Progress, 610));
        t.Apply(Ev(20, PlayEventKind.Progress, 300));  // rewound
        t.Apply(Ev(30, PlayEventKind.Progress, 310));
        Assert.Equal(1, t.Record.SeekCount);
        Assert.Equal(22_000, t.Record.WatchedMs);      // 10 + 2 (a backward step credits only the 2 s slack) + 10
    }

    [Fact]
    public void ClientThatKeepsReportingPlayingWithoutPositionMovingIsCappedBySlack()
    {
        var t = Start();
        t.Apply(Ev(10, PlayEventKind.Progress, 10));
        t.Apply(Ev(70, PlayEventKind.Progress, 10));   // claims playing, position frozen for 60 s
        Assert.Equal(12_000, t.Record.WatchedMs);      // 10 + slack 2
    }

    [Fact]
    public void AudioTrackChangesAreRecordedAndDominantTrackKept()
    {
        var t = Start();
        t.Apply(Ev(10, PlayEventKind.Progress, 10, audio: 1));
        t.Apply(Ev(20, PlayEventKind.Progress, 20, audio: 2));
        t.Apply(Ev(600, PlayEventKind.Stop, 600, audio: 2));
        Assert.Equal(2, t.Record.AudioStreamIndex);
        Assert.Contains("\"index\":2", t.Record.StreamChanges);
    }

    [Fact]
    public void TwoSecondClickIsRecorded()
    {
        var t = Start();
        t.Apply(Ev(2, PlayEventKind.Stop, 2));
        Assert.Equal(2_000, t.Record.WatchedMs);
        Assert.Equal(PlayStates.Closed, t.Record.State);
    }

    [Fact]
    public void CloseAtLastEventDoesNotCreditSilence()
    {
        var t = Start();
        t.Apply(Ev(30, PlayEventKind.Progress, 30));
        t.CloseAt(t.Record.LastEventAt);
        Assert.Equal(30_000, t.Record.WatchedMs);
        Assert.Equal(At(30), t.Record.EndedAt);
    }

    [Fact]
    public void SnapshotClearsDirtyAndIsACopy()
    {
        var t = Start();
        t.Apply(Ev(10, PlayEventKind.Progress, 10));
        Assert.True(t.Dirty);
        var snap = t.Snapshot();
        Assert.False(t.Dirty);
        t.Apply(Ev(20, PlayEventKind.Progress, 20));
        Assert.Equal(10_000, snap.WatchedMs);
    }
}
