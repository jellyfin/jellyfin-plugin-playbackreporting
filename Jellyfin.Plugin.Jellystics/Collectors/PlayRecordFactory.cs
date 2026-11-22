using System.Collections.Concurrent;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jellystics.Models;
using Jellyfin.Plugin.Jellystics.Tracking;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.Jellystics.Collectors;

/// <summary>Maps Jellyfin playback event args to plugin records / events.</summary>
public sealed class PlayRecordFactory
{
    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _mediaSources;
    // Media streams of the items being played: queried once, evicted when the play closes (FinalizeClosed).
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<MediaStream>> _streams = new();

    public PlayRecordFactory(ILibraryManager library, IMediaSourceManager mediaSources)
    {
        _library = library;
        _mediaSources = mediaSources;
    }

    public static string Key(PlaybackProgressEventArgs e) =>
        !string.IsNullOrEmpty(e.PlaySessionId) ? e.PlaySessionId : $"{e.DeviceId}|{UserId(e)}|{e.Item?.Id:N}";

    public static string DeviceUserKey(PlaybackProgressEventArgs e) => $"{e.DeviceId}|{UserId(e)}";

    public static string UserId(PlaybackProgressEventArgs e) => e.Users.Count > 0 ? e.Users[0].Id.ToString("N") : string.Empty;

    public PlayRecord Create(PlaybackProgressEventArgs e)
    {
        var item = e.Item!;
        var r = new PlayRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            PlaySessionId = e.PlaySessionId,
            UserId = UserId(e),
            UserName = e.Users.Count > 0 ? e.Users[0].Username : null,
            ItemId = item.Id.ToString("N"),
            ItemName = item.Name,
            ItemType = item.GetBaseItemKind().ToString(),
            ParentIndexNumber = item.ParentIndexNumber,
            IndexNumber = item.IndexNumber,
            RunTimeTicks = item.RunTimeTicks,
            MediaSourceId = e.MediaSourceId,
            LibraryId = _library.GetCollectionFolders(item).FirstOrDefault()?.Id.ToString("N"),
            DeviceId = e.DeviceId,
            DeviceName = e.DeviceName,
            Client = e.ClientName,
            ApplicationVersion = e.Session?.ApplicationVersion,
            RemoteEndPoint = e.Session?.RemoteEndPoint,
            IsRemote = NetworkClassifier.IsRemote(e.Session?.RemoteEndPoint),
            PlayMethod = e.Session?.PlayState?.PlayMethod?.ToString(),
        };
        if (item is Episode ep) { r.SeriesId = N(ep.SeriesId); r.SeriesName = ep.SeriesName; r.SeasonId = N(ep.SeasonId); }
        if (item is Audio au) { r.AlbumId = au.AlbumEntity is { } album ? N(album.Id) : null; r.AlbumName = au.Album; }
        PrepareTranscodeUpdate(e)(r);
        return r;
    }

    public PlayEvent ToEvent(PlaybackProgressEventArgs e, PlayEventKind kind, DateTimeOffset at, bool? playedToCompletion = null)
    {
        var state = e.Session?.PlayState;
        var streams = e.Item is null ? Array.Empty<MediaStream>() : Streams(e.Item.Id);
        var audio = state?.AudioStreamIndex;
        var sub = state?.SubtitleStreamIndex is >= 0 ? state.SubtitleStreamIndex : null;
        return new PlayEvent(
            at, kind, e.PlaybackPositionTicks, e.IsPaused, e.IsAutomated,
            audio, streams.FirstOrDefault(s => s.Index == audio)?.Language,
            sub, streams.FirstOrDefault(s => s.Index == sub)?.Language,
            playedToCompletion);
    }

    private static string? N(Guid id) => id == Guid.Empty ? null : id.ToString("N");

    /// <summary>
    /// Computes (outside any lock, from the cached media streams) everything derived from the session / transcoding info,
    /// and returns the plain assignments to run on the record (inside <c>PlaySessionRegistry.Mutate</c> for open plays).
    /// </summary>
    public Action<PlayRecord> PrepareTranscodeUpdate(PlaybackProgressEventArgs e)
    {
        var s = e.Session;
        var streams = e.Item is null ? Array.Empty<MediaStream>() : Streams(e.Item.Id);
        var video = streams.FirstOrDefault(x => x.Type == MediaStreamType.Video);
        var audioIdx = s?.PlayState?.AudioStreamIndex;
        var audioFrom = (streams.FirstOrDefault(x => x.Index == audioIdx && x.Type == MediaStreamType.Audio) ?? streams.FirstOrDefault(x => x.Type == MediaStreamType.Audio))?.Codec;
        var method = s?.PlayState?.PlayMethod?.ToString();
        var range = video is null || video.VideoRange == VideoRange.Unknown ? null : video.VideoRange.ToString();
        var rangeType = video is null || video.VideoRangeType == VideoRangeType.Unknown ? null : video.VideoRangeType.ToString();
        var t = s?.TranscodingInfo;
        string? reasons = null, hw = null;
        if (t is not null)
        {
            reasons = JsonSerializer.Serialize(Enum.GetValues<TranscodeReason>().Where(x => x != 0 && t.TranscodeReasons.HasFlag(x)).Select(x => x.ToString()));
            hw = t.HardwareAccelerationType is null or HardwareAccelerationType.none ? null : t.HardwareAccelerationType.ToString();
        }

        return r =>
        {
            r.PlayMethod = method ?? r.PlayMethod;
            r.VideoCodecFrom = video?.Codec;
            r.VideoRange = range;
            r.VideoRangeType = rangeType;
            r.SourceWidth = video?.Width;
            r.SourceHeight = video?.Height;
            r.AudioCodecFrom = audioFrom;
            r.AudioCodec = audioFrom;
            if (t is null) return;
            r.IsVideoDirect = t.IsVideoDirect;
            r.IsAudioDirect = t.IsAudioDirect;
            r.VideoCodecTo = t.IsVideoDirect ? video?.Codec : t.VideoCodec;
            r.AudioCodecTo = t.IsAudioDirect ? audioFrom : t.AudioCodec;
            r.Width = t.Width;
            r.Height = t.Height;
            r.Bitrate = t.Bitrate;
            r.Framerate = t.Framerate;
            r.HwAccel = hw;
            r.TranscodeReasons = reasons;
        };
    }

    /// <summary>On a closed play: the audio codec follows the dominant track chosen by the tracker; frees the cached streams of the item.</summary>
    public void FinalizeClosed(PlayRecord r)
    {
        if (r.State != PlayStates.Closed || !Guid.TryParse(r.ItemId, out var itemId)) return;
        if (r.AudioStreamIndex is { } idx && Streams(itemId).FirstOrDefault(x => x.Index == idx && x.Type == MediaStreamType.Audio) is { } a)
        {
            r.AudioCodecFrom = a.Codec;
            r.AudioCodec = a.Codec;
            if (r.IsAudioDirect == true) r.AudioCodecTo = a.Codec;
        }
        _streams.TryRemove(itemId, out _);
    }

    private IReadOnlyList<MediaStream> Streams(Guid itemId) => _streams.GetOrAdd(itemId, id =>
    {
        try { return _mediaSources.GetMediaStreams(id); }
        catch (ArgumentException) { return Array.Empty<MediaStream>(); }
    });
}
