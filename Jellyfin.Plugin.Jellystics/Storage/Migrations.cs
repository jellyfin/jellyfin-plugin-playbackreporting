namespace Jellyfin.Plugin.Jellystics.Storage;

internal static class Migrations
{
    /// <summary>Ordered DDL scripts; index + 1 = schema version.</summary>
    public static readonly string[] Scripts =
    {
        """
        CREATE TABLE version_seq (id INTEGER PRIMARY KEY CHECK (id = 1), value INTEGER NOT NULL);
        INSERT INTO version_seq (id, value) VALUES (1, 0);
        CREATE TABLE Plays (
          Id TEXT PRIMARY KEY, PlaySessionId TEXT, UserId TEXT NOT NULL, UserName TEXT,
          ItemId TEXT NOT NULL, ItemName TEXT, ItemType TEXT, SeriesId TEXT, SeriesName TEXT, SeasonId TEXT,
          AlbumId TEXT, AlbumName TEXT, ParentIndexNumber INTEGER, IndexNumber INTEGER, MediaSourceId TEXT, LibraryId TEXT,
          StartedAt TEXT NOT NULL, EndedAt TEXT, WatchedMs INTEGER NOT NULL DEFAULT 0, PausedMs INTEGER NOT NULL DEFAULT 0,
          PauseCount INTEGER NOT NULL DEFAULT 0, SeekCount INTEGER NOT NULL DEFAULT 0, SkippedMs INTEGER NOT NULL DEFAULT 0,
          PositionStartTicks INTEGER, PositionEndTicks INTEGER, RunTimeTicks INTEGER, PlayedToCompletion INTEGER,
          DeviceId TEXT, DeviceName TEXT, Client TEXT, ApplicationVersion TEXT, RemoteEndPoint TEXT, IsRemote INTEGER, PlayMethod TEXT,
          AudioStreamIndex INTEGER, AudioLanguage TEXT, AudioCodec TEXT, SubtitleStreamIndex INTEGER, SubtitleLanguage TEXT,
          StreamChanges TEXT NOT NULL DEFAULT '[]',
          VideoCodecFrom TEXT, VideoCodecTo TEXT, AudioCodecFrom TEXT, AudioCodecTo TEXT, Width INTEGER, Height INTEGER,
          Bitrate INTEGER, Framerate REAL, HwAccel TEXT, TranscodeReasons TEXT, VideoRange TEXT,
          IsVideoDirect INTEGER, IsAudioDirect INTEGER, SourceWidth INTEGER, SourceHeight INTEGER, VideoRangeType TEXT,
          State TEXT NOT NULL, LastEventAt TEXT NOT NULL, Source TEXT NOT NULL, Version INTEGER NOT NULL);
        CREATE INDEX ix_plays_version ON Plays (Version);
        CREATE INDEX ix_plays_user_started ON Plays (UserId, StartedAt);
        CREATE INDEX ix_plays_item ON Plays (ItemId);
        CREATE INDEX ix_plays_device ON Plays (DeviceId, UserId, ItemId, StartedAt);
        CREATE TABLE LibraryEvents (
          Id TEXT PRIMARY KEY, Kind TEXT NOT NULL, At TEXT NOT NULL, ItemId TEXT NOT NULL, ItemType TEXT, ItemName TEXT,
          SeriesId TEXT, AlbumId TEXT, LibraryId TEXT, RunTimeTicks INTEGER, SizeBytes INTEGER, Version INTEGER NOT NULL);
        CREATE INDEX ix_libev_version ON LibraryEvents (Version);
        CREATE TABLE UserDataEvents (
          Id TEXT PRIMARY KEY, At TEXT NOT NULL, UserId TEXT NOT NULL, ItemId TEXT NOT NULL, Kind TEXT NOT NULL, Reason TEXT, Version INTEGER NOT NULL);
        CREATE INDEX ix_udev_version ON UserDataEvents (Version);
        CREATE TABLE Logins (
          Id TEXT PRIMARY KEY, At TEXT NOT NULL, UserId TEXT, UserName TEXT, DeviceId TEXT, DeviceName TEXT, Client TEXT,
          ApplicationVersion TEXT, RemoteEndPoint TEXT, Success INTEGER NOT NULL, Version INTEGER NOT NULL);
        CREATE INDEX ix_logins_version ON Logins (Version);
        CREATE TABLE Meta (Key TEXT PRIMARY KEY, Value TEXT);
        """,
        // v2: precise data. LibraryEvents.DateCreated + "present" kind for snapshot rows, Plays.DurationRepaired.
        // Pre-v2 "added" rows came from the snapshot or from live adds stamped with DateCreated: no precise live-add time exists, so they become "present"
        // (DateCreated = At) and get a fresh Version so sync consumers re-read them.
        """
        ALTER TABLE LibraryEvents ADD COLUMN DateCreated TEXT;
        ALTER TABLE Plays ADD COLUMN DurationRepaired INTEGER;
        UPDATE LibraryEvents SET DateCreated = At WHERE Kind = 'added';   -- re-add rows after a removal keep "added" but get a DateCreated
        CREATE TEMP TABLE _remap AS SELECT Id, ROW_NUMBER() OVER (ORDER BY Version) AS n FROM LibraryEvents l
          WHERE Kind = 'added' AND Version = (SELECT MIN(Version) FROM LibraryEvents l2 WHERE l2.ItemId = l.ItemId AND l2.Kind = 'added');   -- only the first add per item is the snapshot-era row
        UPDATE LibraryEvents SET Kind = 'present',
          Version = (SELECT value FROM version_seq) + (SELECT n FROM _remap WHERE _remap.Id = LibraryEvents.Id)
          WHERE Id IN (SELECT Id FROM _remap);
        UPDATE version_seq SET value = value + (SELECT COUNT(*) FROM _remap);
        DROP TABLE _remap;
        """,
    };
}
