using Jellyfin.Plugin.Jellystics.Models;
using Jellyfin.Plugin.Jellystics.Storage;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class PlayStoreTests
{
    private static PlayRecord Play(string id, DateTimeOffset start, string state = PlayStates.Open) => new()
    {
        Id = id, UserId = "u1", ItemId = "i1", StartedAt = start, LastEventAt = start.AddMinutes(10), State = state, WatchedMs = 1000,
    };

    [Fact]
    public void MigrateIsIdempotent()
    {
        using var t = new TempDb();
        t.Db.Migrate();
        Assert.Equal(0, new PlayStore(t.Db).Cursor());
    }

    [Fact]
    public void EveryWriteGetsANewMonotonicVersionAndSinceReturnsChangesInOrder()
    {
        using var t = new TempDb();
        var store = new PlayStore(t.Db);
        var start = new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero);
        store.Upsert(new[] { Play("a", start), Play("b", start) });
        Assert.Equal(2, store.Cursor());
        var updated = Play("a", start, PlayStates.Closed);
        updated.WatchedMs = 5000;
        store.Upsert(new[] { updated });
        var since1 = store.Since(1, 100);
        Assert.Equal(new[] { "b", "a" }, since1.Select(p => p.Id));
        Assert.Equal(3, since1[^1].Version);
        Assert.Equal(5000, store.Get("a")!.WatchedMs);
        Assert.Equal(PlayStates.Closed, store.Get("a")!.State);
    }

    [Fact]
    public void CloseOpenPlaysClosesAtLastEventAndBumpsVersion()
    {
        using var t = new TempDb();
        var store = new PlayStore(t.Db);
        var start = new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero);
        store.Upsert(new[] { Play("open", start), Play("done", start, PlayStates.Closed) });
        Assert.Equal(1, store.CloseOpenPlays());
        var p = store.Get("open")!;
        Assert.Equal(PlayStates.Closed, p.State);
        Assert.Equal(start.AddMinutes(10), p.EndedAt);
        Assert.Equal(3, p.Version);
    }

    [Fact]
    public void RoundTripsAllColumns()
    {
        using var t = new TempDb();
        var store = new PlayStore(t.Db);
        var start = new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero);
        var p = Play("x", start);
        p.IsRemote = true; p.PlayedToCompletion = false; p.Framerate = 23.976f; p.TranscodeReasons = "[\"VideoCodecNotSupported\"]";
        p.StreamChanges = "[{\"kind\":\"audio\",\"index\":2}]"; p.SeekCount = 3; p.SkippedMs = 90000; p.RunTimeTicks = 27_000_000_000;
        store.Upsert(new[] { p });
        var r = store.Get("x")!;
        Assert.True(r.IsRemote); Assert.False(r.PlayedToCompletion); Assert.Equal(23.976f, r.Framerate!.Value, 3);
        Assert.Equal(p.TranscodeReasons, r.TranscodeReasons); Assert.Equal(p.StreamChanges, r.StreamChanges);
        Assert.Equal(3, r.SeekCount); Assert.Equal(90000, r.SkippedMs); Assert.Equal(start, r.StartedAt);
    }

    [Fact]
    public void ExistsOverlappingFindsSameUserItemSourceWithTimeOverlap()
    {
        using var t = new TempDb();
        var store = new PlayStore(t.Db);
        var start = new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero);
        var p = Play("p", start, PlayStates.Closed); p.EndedAt = start.AddMinutes(40);
        store.Upsert(new[] { p });
        Assert.True(store.ExistsOverlapping("u1", "i1", start.AddMinutes(30), start.AddMinutes(90), PlaySources.Plugin));
        Assert.False(store.ExistsOverlapping("u1", "i1", start.AddMinutes(41), start.AddMinutes(90), PlaySources.Plugin));
        Assert.False(store.ExistsOverlapping("u2", "i1", start, start.AddMinutes(10), PlaySources.Plugin));
    }
}
