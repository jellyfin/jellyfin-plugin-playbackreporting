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
        if (item is Episode ep) { r.SeriesId = ep.SeriesId.ToString("N"); r.SeriesName = ep.SeriesName; r.SeasonId = ep.SeasonId.ToString("N"); }
        if (item is Audio au) { r.AlbumId = au.AlbumEntity?.Id.ToString("N"); r.AlbumName = au.Album; }
        var streams = Streams(item.Id);
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        r.VideoCodecFrom = video?.Codec;
        r.VideoRange = video?.VideoRange.ToString();
        ApplyTranscoding(r, e.Session, streams);
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

    public void ApplyTranscoding(PlayRecord r, SessionInfo? s, IReadOnlyList<MediaStream>? streams = null)
    {
        r.PlayMethod = s?.PlayState?.PlayMethod?.ToString() ?? r.PlayMethod;
        var audioIdx = s?.PlayState?.AudioStreamIndex;
        streams ??= Streams(Guid.Parse(r.ItemId));
        r.AudioCodecFrom = streams.FirstOrDefault(x => x.Index == audioIdx)?.Codec ?? streams.FirstOrDefault(x => x.Type == MediaStreamType.Audio)?.Codec;
        r.AudioCodec = r.AudioCodecFrom;
        var t = s?.TranscodingInfo;
        if (t is null) return;
        r.VideoCodecTo = t.IsVideoDirect ? r.VideoCodecFrom : t.VideoCodec;
        r.AudioCodecTo = t.IsAudioDirect ? r.AudioCodecFrom : t.AudioCodec;
        r.Width = t.Width;
        r.Height = t.Height;
        r.Bitrate = t.Bitrate;
        r.Framerate = t.Framerate;
        r.HwAccel = t.HardwareAccelerationType?.ToString();
        r.TranscodeReasons = JsonSerializer.Serialize(Enum.GetValues<TranscodeReason>().Where(x => x != 0 && t.TranscodeReasons.HasFlag(x)).Select(x => x.ToString()));
    }

    private IReadOnlyList<MediaStream> Streams(Guid itemId)
    {
        try { return _mediaSources.GetMediaStreams(itemId); }
        catch (ArgumentException) { return Array.Empty<MediaStream>(); }
    }
}
