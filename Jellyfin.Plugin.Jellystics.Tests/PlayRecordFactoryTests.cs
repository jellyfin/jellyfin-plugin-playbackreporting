using System.Reflection;
using Jellyfin.Plugin.Jellystics.Collectors;
using Jellyfin.Plugin.Jellystics.Models;
using Jellyfin.Plugin.Jellystics.Tracking;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class PlayRecordFactoryTests
{
    public class Stub<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = (_, _) => null;
        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            var r = Handler(m!, args);
            if (r is not null) return r;
            var rt = m!.ReturnType;
            if (rt == typeof(void)) return null;
            if (rt.IsValueType) return Activator.CreateInstance(rt);
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(List<>)) return Activator.CreateInstance(rt);
            return null;
        }
        public static T Create(Func<MethodInfo, object?[]?, object?> h) { var p = DispatchProxy.Create<T, Stub<T>>(); ((Stub<T>)(object)p).Handler = h; return p; }
    }

    private static PlayRecordFactory Factory(List<MediaStream> streams, out int[] calls)
    {
        var counter = new int[1];
        var lib = Stub<ILibraryManager>.Create((_, _) => null);
        var ms = Stub<IMediaSourceManager>.Create((m, _) => { if (m.Name == "GetMediaStreams") { counter[0]++; return streams; } return null; });
        calls = counter;
        return new PlayRecordFactory(lib, ms);
    }

    private static List<MediaStream> Streams() => new()
    {
        new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "hevc", Width = 3840, Height = 2160, BitDepth = 10, ColorTransfer = "smpte2084", ColorPrimaries = "bt2020", ColorSpace = "bt2020nc" },
        new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "eac3", Language = "eng" },
        new MediaStream { Index = 2, Type = MediaStreamType.Audio, Codec = "aac", Language = "fre" },
        new MediaStream { Index = 3, Type = MediaStreamType.Subtitle, Codec = "srt", Language = "fre" },
    };

    private static PlaybackProgressEventArgs Args(BaseItem item, SessionInfo? session = null)
    {
        var e = new PlaybackProgressEventArgs { Item = item, PlaySessionId = "ps1", DeviceId = "dev", MediaSourceId = "ms", Session = session };
        e.Users.Add(new Jellyfin.Database.Implementations.Entities.User("alice", "default", "default"));
        return e;
    }

    private static SessionInfo Session(TranscodingInfo? t, int? audio = null)
    {
        var s = new SessionInfo(null!, null!) { RemoteEndPoint = "192.168.1.5", ApplicationVersion = "1.0", TranscodingInfo = t };
        s.PlayState.AudioStreamIndex = audio;
        s.PlayState.PlayMethod = t is null ? PlayMethod.DirectPlay : PlayMethod.Transcode;
        return s;
    }

    [Fact]
    public void MapsAMovieWithTranscodingAndSourceInfo()
    {
        var f = Factory(Streams(), out _);
        var t = new TranscodingInfo { IsVideoDirect = false, IsAudioDirect = true, VideoCodec = "h264", AudioCodec = "aac", Width = 1920, Height = 1080, Bitrate = 8000, Framerate = 23.976f, HardwareAccelerationType = HardwareAccelerationType.none, TranscodeReasons = TranscodeReason.VideoCodecNotSupported };
        var r = f.Create(Args(new Movie { Id = Guid.NewGuid(), Name = "Atlas", RunTimeTicks = 5400 * 10_000_000L }, Session(t, 1)));
        Assert.Equal("Movie", r.ItemType);
        Assert.Equal("hevc", r.VideoCodecFrom); Assert.Equal("h264", r.VideoCodecTo);
        Assert.Equal("eac3", r.AudioCodecFrom); Assert.Equal("eac3", r.AudioCodecTo);
        Assert.False(r.IsVideoDirect); Assert.True(r.IsAudioDirect);
        Assert.Equal(3840, r.SourceWidth); Assert.Equal(2160, r.SourceHeight); Assert.Equal(1920, r.Width);
        Assert.Equal("HDR10", r.VideoRangeType);
        Assert.Null(r.HwAccel);
        Assert.Contains("VideoCodecNotSupported", r.TranscodeReasons);
        Assert.Equal(false, r.IsRemote);
    }

    [Fact]
    public void NoTranscodingInfoLeavesDirectFlagsNull()
    {
        var f = Factory(Streams(), out _);
        var r = f.Create(Args(new Movie { Id = Guid.NewGuid() }, Session(null)));
        Assert.Null(r.IsVideoDirect); Assert.Null(r.IsAudioDirect); Assert.Null(r.VideoCodecTo);
    }

    [Fact]
    public void HardwareAccelerationIsKeptWhenReal()
    {
        var f = Factory(Streams(), out _);
        var t = new TranscodingInfo { VideoCodec = "h264", HardwareAccelerationType = HardwareAccelerationType.vaapi };
        Assert.Equal("vaapi", f.Create(Args(new Movie { Id = Guid.NewGuid() }, Session(t))).HwAccel);
    }

    [Fact]
    public void UnknownVideoRangeIsNull()
    {
        var f = Factory(new() { new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac" } }, out _);
        Assert.Null(f.Create(Args(new Movie { Id = Guid.NewGuid() })).VideoRange);
    }

    [Fact]
    public void EmptyGuidsBecomeNull()
    {
        var f = Factory(Streams(), out _);
        var r = f.Create(Args(new Episode { Id = Guid.NewGuid(), Name = "E", SeriesId = Guid.Empty, SeasonId = Guid.Empty }));
        Assert.Equal("Episode", r.ItemType);
        Assert.Null(r.SeriesId); Assert.Null(r.SeasonId);
    }

    [Fact]
    public void MapsAnAudioTrack()
    {
        var f = Factory(Streams(), out _);
        var r = f.Create(Args(new Audio { Id = Guid.NewGuid(), Name = "Song", Album = "LP" }));
        Assert.Equal("Audio", r.ItemType); Assert.Equal("LP", r.AlbumName);
    }

    [Fact]
    public void StreamsAreQueriedOncePerItemUntilTheItemIsClosed()
    {
        var f = Factory(Streams(), out var calls);
        var item = new Movie { Id = Guid.NewGuid() };
        var e = Args(item, Session(null, 2));
        var rec = f.Create(e);
        f.ToEvent(e, PlayEventKind.Progress, default);
        f.ToEvent(e, PlayEventKind.Progress, default);
        f.PrepareTranscodeUpdate(e)(rec);
        Assert.Equal(1, calls[0]);
        rec.State = PlayStates.Closed;
        f.FinalizeClosed(rec);
        f.ToEvent(e, PlayEventKind.Progress, default);
        Assert.Equal(2, calls[0]);
    }

    [Fact]
    public void FinalizeClosedSetsTheCodecOfTheDominantAudioTrack()
    {
        var f = Factory(Streams(), out _);
        var item = new Movie { Id = Guid.NewGuid() };
        var rec = f.Create(Args(item, Session(null, 1)));
        Assert.Equal("eac3", rec.AudioCodecFrom);
        rec.AudioStreamIndex = 2; rec.State = PlayStates.Closed;
        f.FinalizeClosed(rec);
        Assert.Equal("aac", rec.AudioCodecFrom);
        Assert.Equal("aac", rec.AudioCodec);
    }

    [Fact]
    public void ToEventResolvesLanguages()
    {
        var f = Factory(Streams(), out _);
        var e = Args(new Movie { Id = Guid.NewGuid() }, Session(null, 2));
        e.Session!.PlayState.SubtitleStreamIndex = 3;
        var ev = f.ToEvent(e, PlayEventKind.Progress, default);
        Assert.Equal("fre", ev.AudioLanguage); Assert.Equal("fre", ev.SubtitleLanguage);
    }

    [Fact]
    public void IdleStreamCachesAreEvictedBySweep()
    {
        var clock = new TestClock();
        var calls = new int[1];
        var ms = Stub<IMediaSourceManager>.Create((m, _) => { if (m.Name == "GetMediaStreams") { calls[0]++; return Streams(); } return null; });
        var f = new PlayRecordFactory(Stub<ILibraryManager>.Create((_, _) => null), ms, clock);
        var e = Args(new Movie { Id = Guid.NewGuid() }, Session(null));
        f.ToEvent(e, PlayEventKind.Progress, default);
        clock.Advance(TimeSpan.FromMinutes(5));
        f.EvictIdle(TimeSpan.FromMinutes(10));
        f.ToEvent(e, PlayEventKind.Progress, default);          // still cached, and refreshes its last use
        Assert.Equal(1, calls[0]);
        clock.Advance(TimeSpan.FromMinutes(11));
        f.EvictIdle(TimeSpan.FromMinutes(10));
        f.ToEvent(e, PlayEventKind.Progress, default);
        Assert.Equal(2, calls[0]);
    }
}
