using Jellyfin.Plugin.Jellystics.Models;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Jellystics.Storage;

/// <summary>Reads/writes Plays. Every upsert assigns a new global Version.</summary>
public sealed class PlayStore
{
    private static readonly string[] Cols =
    {
        "Id", "PlaySessionId", "UserId", "UserName", "ItemId", "ItemName", "ItemType", "SeriesId", "SeriesName", "SeasonId",
        "AlbumId", "AlbumName", "ParentIndexNumber", "IndexNumber", "MediaSourceId", "LibraryId", "StartedAt", "EndedAt",
        "WatchedMs", "PausedMs", "PauseCount", "SeekCount", "SkippedMs", "PositionStartTicks", "PositionEndTicks", "RunTimeTicks",
        "PlayedToCompletion", "DeviceId", "DeviceName", "Client", "ApplicationVersion", "RemoteEndPoint", "IsRemote", "PlayMethod",
        "AudioStreamIndex", "AudioLanguage", "AudioCodec", "SubtitleStreamIndex", "SubtitleLanguage", "StreamChanges",
        "VideoCodecFrom", "VideoCodecTo", "AudioCodecFrom", "AudioCodecTo", "Width", "Height", "Bitrate", "Framerate", "HwAccel",
        "TranscodeReasons", "VideoRange", "IsVideoDirect", "IsAudioDirect", "SourceWidth", "SourceHeight", "VideoRangeType", "State", "LastEventAt", "Source", "DurationRepaired", "Version",
    };

    private static readonly string UpsertSql =
        $"INSERT INTO Plays ({string.Join(",", Cols)}) VALUES ({string.Join(",", Cols.Select(c => "$" + c))}) " +
        $"ON CONFLICT(Id) DO UPDATE SET {string.Join(",", Cols.Where(c => c != "Id").Select(c => $"{c}=excluded.{c}"))}";

    private readonly JellysticsDb _db;

    public PlayStore(JellysticsDb db) => _db = db;

    public void Upsert(IReadOnlyCollection<PlayRecord> plays)
    {
        if (plays.Count == 0) return;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        foreach (var p in plays)
        {
            p.Version = JellysticsDb.NextVersion(c, tx);
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = UpsertSql;
            Bind(cmd, p);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<PlayRecord> Since(long since, int limit) =>
        Query("SELECT * FROM Plays WHERE Version > $since ORDER BY Version LIMIT $limit", ("$since", since), ("$limit", limit));

    public PlayRecord? Get(string id) => Query("SELECT * FROM Plays WHERE Id = $id", ("$id", id)).FirstOrDefault();

    public long Count() => Scalar("SELECT COUNT(*) FROM Plays");

    public DateTimeOffset? FirstPlayAt()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MIN(StartedAt) FROM Plays";
        return cmd.ExecuteScalar() is string s ? JellysticsDb.ParseIso(s) : null;
    }

    private long Scalar(string sql)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Which of the given ids exist, in chunks of 500 (one IN query per chunk).</summary>
    /// <summary>Of the given ids, those whose DurationRepaired is still NULL (rows written before schema v2).</summary>
    public HashSet<string> IdsWithoutRepairFlag(IEnumerable<string> ids)
    {
        var found = new HashSet<string>();
        using var c = _db.Open();
        foreach (var chunk in ids.Distinct().Chunk(500))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT Id FROM Plays WHERE DurationRepaired IS NULL AND Id IN ({string.Join(",", chunk.Select((_, i) => "$p" + i))})";
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue("$p" + i, chunk[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) found.Add(r.GetString(0));
        }
        return found;
    }

    /// <summary>Sets DurationRepaired on existing rows without touching any other column (new Version so sync consumers re-read them).</summary>
    public void SetDurationRepaired(IReadOnlyDictionary<string, bool> flags)
    {
        if (flags.Count == 0) return;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        foreach (var (id, flag) in flags)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Plays SET DurationRepaired = $f, Version = $v WHERE Id = $id AND DurationRepaired IS NULL";
            cmd.Parameters.AddWithValue("$f", flag ? 1 : 0);
            cmd.Parameters.AddWithValue("$v", JellysticsDb.NextVersion(c, tx));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public HashSet<string> ExistingIds(IEnumerable<string> ids)
    {
        var found = new HashSet<string>();
        using var c = _db.Open();
        foreach (var chunk in ids.Distinct().Chunk(500))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT Id FROM Plays WHERE Id IN ({string.Join(",", chunk.Select((_, i) => "$p" + i))})";
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue("$p" + i, chunk[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) found.Add(r.GetString(0));
        }
        return found;
    }

    public long Cursor() { using var c = _db.Open(); return JellysticsDb.CurrentVersion(c); }

    /// <summary>Closes plays left open (crash / restart) at their last event. Returns how many.</summary>
    /// <remarks>Dominant audio track is not computed here (acceptable for crash recovery). One Upsert = one transaction.</remarks>
    public int CloseOpenPlays()
    {
        var open = Query("SELECT * FROM Plays WHERE State = 'open'");
        foreach (var p in open) { p.State = PlayStates.Closed; p.EndedAt ??= p.LastEventAt; }
        Upsert(open);
        return open.Count;
    }

    public bool ExistsOverlapping(string userId, string itemId, DateTimeOffset start, DateTimeOffset end, string source)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM Plays WHERE UserId=$u AND ItemId=$i AND Source=$s AND StartedAt < $end AND COALESCE(EndedAt, LastEventAt) > $start LIMIT 1";
        cmd.Parameters.AddWithValue("$u", userId); cmd.Parameters.AddWithValue("$i", itemId); cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$start", JellysticsDb.Iso(start)); cmd.Parameters.AddWithValue("$end", JellysticsDb.Iso(end));
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Deletes closed plays only (never a play in progress), without tombstones: sync consumers (cursor on Version) will not see these deletions.</summary>
    public int PurgeBefore(DateTimeOffset before)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM Plays WHERE StartedAt < $b AND State = 'closed'";
        cmd.Parameters.AddWithValue("$b", JellysticsDb.Iso(before));
        return cmd.ExecuteNonQuery();
    }

    private List<PlayRecord> Query(string sql, params (string Name, object Value)[] args)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        using var r = cmd.ExecuteReader();
        var list = new List<PlayRecord>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    private static object Db(object? v) => v ?? DBNull.Value;

    private static void Bind(SqliteCommand cmd, PlayRecord p)
    {
        var values = new Dictionary<string, object?>
        {
            ["Id"] = p.Id, ["PlaySessionId"] = p.PlaySessionId, ["UserId"] = p.UserId, ["UserName"] = p.UserName,
            ["ItemId"] = p.ItemId, ["ItemName"] = p.ItemName, ["ItemType"] = p.ItemType, ["SeriesId"] = p.SeriesId,
            ["SeriesName"] = p.SeriesName, ["SeasonId"] = p.SeasonId, ["AlbumId"] = p.AlbumId, ["AlbumName"] = p.AlbumName,
            ["ParentIndexNumber"] = p.ParentIndexNumber, ["IndexNumber"] = p.IndexNumber, ["MediaSourceId"] = p.MediaSourceId,
            ["LibraryId"] = p.LibraryId, ["StartedAt"] = JellysticsDb.Iso(p.StartedAt), ["EndedAt"] = JellysticsDb.Iso(p.EndedAt),
            ["WatchedMs"] = p.WatchedMs, ["PausedMs"] = p.PausedMs, ["PauseCount"] = p.PauseCount, ["SeekCount"] = p.SeekCount,
            ["SkippedMs"] = p.SkippedMs, ["PositionStartTicks"] = p.PositionStartTicks, ["PositionEndTicks"] = p.PositionEndTicks,
            ["RunTimeTicks"] = p.RunTimeTicks, ["PlayedToCompletion"] = p.PlayedToCompletion, ["DeviceId"] = p.DeviceId,
            ["DeviceName"] = p.DeviceName, ["Client"] = p.Client, ["ApplicationVersion"] = p.ApplicationVersion,
            ["RemoteEndPoint"] = p.RemoteEndPoint, ["IsRemote"] = p.IsRemote, ["PlayMethod"] = p.PlayMethod,
            ["AudioStreamIndex"] = p.AudioStreamIndex, ["AudioLanguage"] = p.AudioLanguage, ["AudioCodec"] = p.AudioCodec,
            ["SubtitleStreamIndex"] = p.SubtitleStreamIndex, ["SubtitleLanguage"] = p.SubtitleLanguage, ["StreamChanges"] = p.StreamChanges,
            ["VideoCodecFrom"] = p.VideoCodecFrom, ["VideoCodecTo"] = p.VideoCodecTo, ["AudioCodecFrom"] = p.AudioCodecFrom,
            ["AudioCodecTo"] = p.AudioCodecTo, ["Width"] = p.Width, ["Height"] = p.Height, ["Bitrate"] = p.Bitrate,
            ["Framerate"] = p.Framerate, ["HwAccel"] = p.HwAccel, ["TranscodeReasons"] = p.TranscodeReasons, ["VideoRange"] = p.VideoRange, ["IsVideoDirect"] = p.IsVideoDirect, ["IsAudioDirect"] = p.IsAudioDirect, ["SourceWidth"] = p.SourceWidth, ["SourceHeight"] = p.SourceHeight, ["VideoRangeType"] = p.VideoRangeType,
            ["State"] = p.State, ["LastEventAt"] = JellysticsDb.Iso(p.LastEventAt), ["Source"] = p.Source, ["DurationRepaired"] = p.DurationRepaired, ["Version"] = p.Version,
        };
        foreach (var col in Cols) cmd.Parameters.AddWithValue("$" + col, Db(values[col]));
    }

    private static PlayRecord Read(SqliteDataReader r)
    {
        T? N<T>(string col) where T : struct { var v = r[col]; return v is DBNull ? null : (T)Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
        string? S(string col) => r[col] as string;
        return new PlayRecord
        {
            Id = (string)r["Id"], PlaySessionId = S("PlaySessionId"), UserId = (string)r["UserId"], UserName = S("UserName"),
            ItemId = (string)r["ItemId"], ItemName = S("ItemName"), ItemType = S("ItemType"), SeriesId = S("SeriesId"),
            SeriesName = S("SeriesName"), SeasonId = S("SeasonId"), AlbumId = S("AlbumId"), AlbumName = S("AlbumName"),
            ParentIndexNumber = N<int>("ParentIndexNumber"), IndexNumber = N<int>("IndexNumber"), MediaSourceId = S("MediaSourceId"),
            LibraryId = S("LibraryId"), StartedAt = JellysticsDb.ParseIso(r["StartedAt"])!.Value, EndedAt = JellysticsDb.ParseIso(r["EndedAt"]),
            WatchedMs = (long)r["WatchedMs"], PausedMs = (long)r["PausedMs"], PauseCount = (int)(long)r["PauseCount"],
            SeekCount = (int)(long)r["SeekCount"], SkippedMs = (long)r["SkippedMs"], PositionStartTicks = N<long>("PositionStartTicks"),
            PositionEndTicks = N<long>("PositionEndTicks"), RunTimeTicks = N<long>("RunTimeTicks"),
            PlayedToCompletion = N<long>("PlayedToCompletion") is { } ptc ? ptc != 0 : null,
            DeviceId = S("DeviceId"), DeviceName = S("DeviceName"), Client = S("Client"), ApplicationVersion = S("ApplicationVersion"),
            RemoteEndPoint = S("RemoteEndPoint"), IsRemote = N<long>("IsRemote") is { } rem ? rem != 0 : null, PlayMethod = S("PlayMethod"),
            AudioStreamIndex = N<int>("AudioStreamIndex"), AudioLanguage = S("AudioLanguage"), AudioCodec = S("AudioCodec"),
            SubtitleStreamIndex = N<int>("SubtitleStreamIndex"), SubtitleLanguage = S("SubtitleLanguage"), StreamChanges = (string)r["StreamChanges"],
            VideoCodecFrom = S("VideoCodecFrom"), VideoCodecTo = S("VideoCodecTo"), AudioCodecFrom = S("AudioCodecFrom"),
            AudioCodecTo = S("AudioCodecTo"), Width = N<int>("Width"), Height = N<int>("Height"), Bitrate = N<int>("Bitrate"),
            Framerate = N<float>("Framerate"), HwAccel = S("HwAccel"), TranscodeReasons = S("TranscodeReasons"), VideoRange = S("VideoRange"), IsVideoDirect = N<long>("IsVideoDirect") is { } ivd ? ivd != 0 : null, IsAudioDirect = N<long>("IsAudioDirect") is { } iad ? iad != 0 : null, SourceWidth = N<int>("SourceWidth"), SourceHeight = N<int>("SourceHeight"), VideoRangeType = S("VideoRangeType"),
            State = (string)r["State"], LastEventAt = JellysticsDb.ParseIso(r["LastEventAt"])!.Value, Source = (string)r["Source"],
            DurationRepaired = N<long>("DurationRepaired") is { } dr ? dr != 0 : null,
            Version = (long)r["Version"],
        };
    }
}
