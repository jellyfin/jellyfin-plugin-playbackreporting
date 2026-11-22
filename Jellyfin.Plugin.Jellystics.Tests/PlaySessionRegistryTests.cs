using Jellyfin.Plugin.Jellystics.Models;
using Jellyfin.Plugin.Jellystics.Tracking;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class PlaySessionRegistryTests
{
    private static PlayRecord Rec(string item) => new() { Id = Guid.NewGuid().ToString("N"), UserId = "u", ItemId = item };
    private static PlayEvent Ev(TestClock c, PlayEventKind k, double pos, bool paused = false) => new(c.UtcNow, k, (long)(pos * 10_000_000), paused);

    [Fact]
    public void StartOnSameDeviceAndUserClosesThePreviousPlay()
    {
        var c = new TestClock(); var r = new PlaySessionRegistry(c);
        r.OnEvent("s1", "dev|u", () => Rec("ep1"), Ev(c, PlayEventKind.Start, 0));
        c.Advance(TimeSpan.FromMinutes(40));
        r.OnEvent("s1", "dev|u", () => Rec("ep1"), Ev(c, PlayEventKind.Progress, 2400));
        var closed = r.OnEvent("s2", "dev|u", () => Rec("ep2"), Ev(c, PlayEventKind.Start, 0));
        var prev = Assert.Single(closed);
        Assert.Equal("ep1", prev.ItemId);
        Assert.Equal(PlayStates.Closed, prev.State);
        Assert.Equal(1, r.OpenCount);
    }

    [Fact]
    public void ProgressForUnknownKeyStartsAPlay()
    {
        var c = new TestClock(); var r = new PlaySessionRegistry(c);
        r.OnEvent("s1", "dev|u", () => Rec("m"), Ev(c, PlayEventKind.Progress, 1200));
        Assert.Equal(1, r.OpenCount);
        var snap = Assert.Single(r.TakeDirtyOpen());
        Assert.Equal((long)(1200 * 10_000_000L), snap.PositionStartTicks);
    }

    [Fact]
    public void StopReturnsTheClosedPlayAndForgetsIt()
    {
        var c = new TestClock(); var r = new PlaySessionRegistry(c);
        r.OnEvent("s1", "dev|u", () => Rec("m"), Ev(c, PlayEventKind.Start, 0));
        c.Advance(TimeSpan.FromSeconds(30));
        var closed = r.OnEvent("s1", "dev|u", () => Rec("m"), Ev(c, PlayEventKind.Stop, 30));
        Assert.Equal(30_000, Assert.Single(closed).WatchedMs);
        Assert.Equal(0, r.OpenCount);
    }

    [Fact]
    public void SweepClosesSilentPlayingAfterFiveMinutesAtLastEvent()
    {
        var c = new TestClock(); var r = new PlaySessionRegistry(c);
        r.OnEvent("s1", "dev|u", () => Rec("m"), Ev(c, PlayEventKind.Start, 0));
        c.Advance(TimeSpan.FromSeconds(20));
        r.OnEvent("s1", "dev|u", () => Rec("m"), Ev(c, PlayEventKind.Progress, 20));
        var lastEvent = c.UtcNow;
        c.Advance(TimeSpan.FromMinutes(4));
        Assert.Empty(r.Sweep());
        c.Advance(TimeSpan.FromMinutes(2));
        var closed = Assert.Single(r.Sweep());
        Assert.Equal(lastEvent, closed.EndedAt);
        Assert.Equal(20_000, closed.WatchedMs);
    }

    [Fact]
    public void SweepKeepsPausedPlaysUpToAnHour()
    {
        var c = new TestClock(); var r = new PlaySessionRegistry(c);
        r.OnEvent("s1", "dev|u", () => Rec("m"), Ev(c, PlayEventKind.Start, 0, paused: true));
        c.Advance(TimeSpan.FromMinutes(30));
        Assert.Empty(r.Sweep());
        c.Advance(TimeSpan.FromMinutes(31));
        Assert.Single(r.Sweep());
    }

    [Fact]
    public void TakeDirtyOpenReturnsOnlyChangedOpenPlaysOnce()
    {
        var c = new TestClock(); var r = new PlaySessionRegistry(c);
        r.OnEvent("s1", "dev|u", () => Rec("m"), Ev(c, PlayEventKind.Start, 0));
        Assert.Single(r.TakeDirtyOpen());
        Assert.Empty(r.TakeDirtyOpen());
        c.Advance(TimeSpan.FromSeconds(10));
        r.OnEvent("s1", "dev|u", () => Rec("m"), Ev(c, PlayEventKind.Progress, 10));
        Assert.Single(r.TakeDirtyOpen());
    }

    [Fact]
    public void MutateChangesTheOpenRecordAndMarksItDirty()
    {
        var c = new TestClock(); var r = new PlaySessionRegistry(c);
        r.OnEvent("s1", "dev|u", () => Rec("m"), Ev(c, PlayEventKind.Start, 0));
        r.TakeDirtyOpen();
        r.Mutate("s1", rec => rec.PlayMethod = "Transcode");
        r.Mutate("unknown", rec => rec.PlayMethod = "x");   // no-op
        Assert.Equal("Transcode", Assert.Single(r.TakeDirtyOpen()).PlayMethod);
    }

    [Fact]
    public void StopForAnUnknownKeyDoesNotCloseTheLivePlayOnTheSameDevice()
    {
        var c = new TestClock(); var r = new PlaySessionRegistry(c);
        r.OnEvent("s1", "dev|u", () => Rec("a"), Ev(c, PlayEventKind.Start, 0));
        r.OnEvent("s2", "dev|u", () => Rec("b"), Ev(c, PlayEventKind.Start, 0));
        var closed = r.OnEvent("s1", "dev|u", () => Rec("a"), Ev(c, PlayEventKind.Stop, 5));
        Assert.Empty(closed);
        Assert.Equal(1, r.OpenCount);
    }

    [Fact]
    public void StopOfUnknownKeyWithNothingOpenReturnsNothing()
    {
        var c = new TestClock(); var r = new PlaySessionRegistry(c);
        Assert.Empty(r.OnEvent("x", "dev|u", () => Rec("a"), Ev(c, PlayEventKind.Stop, 5)));
        Assert.Equal(0, r.OpenCount);
    }

    [Fact]
    public void LateEventForAClosedKeyIsIgnored()
    {
        var c = new TestClock(); var r = new PlaySessionRegistry(c);
        r.OnEvent("s1", "dev|u", () => Rec("a"), Ev(c, PlayEventKind.Start, 0));
        c.Advance(TimeSpan.FromSeconds(30));
        r.OnEvent("s1", "dev|u", () => Rec("a"), Ev(c, PlayEventKind.Stop, 30));
        c.Advance(TimeSpan.FromSeconds(1));
        var closed = r.OnEvent("s1", "dev|u", () => Rec("a"), Ev(c, PlayEventKind.Progress, 31));
        Assert.Empty(closed);
        Assert.Equal(0, r.OpenCount);
    }

    [Fact]
    public void ClosedKeysAreForgottenAfterTwoMinutes()
    {
        var c = new TestClock(); var r = new PlaySessionRegistry(c);
        r.OnEvent("s1", "dev|u", () => Rec("a"), Ev(c, PlayEventKind.Start, 0));
        r.OnEvent("s1", "dev|u", () => Rec("a"), Ev(c, PlayEventKind.Stop, 0));
        c.Advance(TimeSpan.FromMinutes(3));
        r.Sweep();
        r.OnEvent("s1", "dev|u", () => Rec("a"), Ev(c, PlayEventKind.Progress, 0));
        Assert.Equal(1, r.OpenCount);
    }

    [Fact]
    public void DuplicateStartForTheSameItemDoesNotSplitThePlay()
    {
        var c = new TestClock(); var r = new PlaySessionRegistry(c);
        r.OnEvent("s1", "dev|u", () => Rec("a"), Ev(c, PlayEventKind.Start, 0));
        c.Advance(TimeSpan.FromSeconds(10));
        var closed = r.OnEvent("s1", "dev|u", () => Rec("a"), Ev(c, PlayEventKind.Start, 10));
        Assert.Empty(closed);
        Assert.Equal(1, r.OpenCount);
        Assert.Equal(10_000, Assert.Single(r.TakeDirtyOpen()).WatchedMs);
    }

    [Fact]
    public void OnEventNowStampsWithTheRegistryClock()
    {
        var c = new TestClock(); var r = new PlaySessionRegistry(c);
        r.OnEventNow("s1", "dev|u", () => Rec("a"), new PlayEvent(default, PlayEventKind.Start, 0, false));
        Assert.Equal(c.UtcNow, Assert.Single(r.TakeDirtyOpen()).StartedAt);
    }

    [Fact]
    public void IsOpenReflectsOpenPlays()
    {
        var c = new TestClock(); var r = new PlaySessionRegistry(c);
        Assert.False(r.IsOpen("s1"));
        r.OnEvent("s1", "dev|u", () => Rec("a"), Ev(c, PlayEventKind.Start, 0));
        Assert.True(r.IsOpen("s1"));
    }

    [Fact]
    public void ClientResumingAfterASweepStartsANewPlayImmediately()
    {
        var c = new TestClock(); var r = new PlaySessionRegistry(c);
        r.OnEvent("s1", "dev|u", () => Rec("m"), Ev(c, PlayEventKind.Start, 0));
        c.Advance(TimeSpan.FromMinutes(6));
        Assert.Single(r.Sweep());
        r.OnEvent("s1", "dev|u", () => Rec("m"), Ev(c, PlayEventKind.Progress, 400));
        Assert.Equal(1, r.OpenCount);
    }

    [Fact]
    public void ResolveKeyFindsTheOpenPlayOfTheSameItemWhenTheKeyDiffers()
    {
        var c = new TestClock(); var r = new PlaySessionRegistry(c);
        r.OnEvent("session-1", "dev|u", () => Rec("m"), Ev(c, PlayEventKind.Start, 0));
        Assert.Equal("session-1", r.ResolveKey("dev|u|m", "dev|u", "m"));
        Assert.Equal("session-1", r.ResolveKey("session-1", "dev|u", "m"));
        Assert.Equal("dev|u|other", r.ResolveKey("dev|u|other", "dev|u", "other"));
    }
}
