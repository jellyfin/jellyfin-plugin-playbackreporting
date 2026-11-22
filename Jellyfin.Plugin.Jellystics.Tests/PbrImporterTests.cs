using Jellyfin.Plugin.Jellystics.Import;
using Jellyfin.Plugin.Jellystics.Models;
using Jellyfin.Plugin.Jellystics.Storage;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class PbrImporterTests : IDisposable
{
    private sealed class FakeItems : IItemLookup
    {
        public ItemInfo? Find(string itemId) => itemId switch
        {
            "ep1" => new ItemInfo("Episode", 45L * 60 * 10_000_000, "s1", "Station Nord", "se1", null, null, 1, 4, "lib-tv"),
            _ => null,
        };
    }

    private readonly List<string> _files = new();
    public void Dispose() { foreach (var f in _files) { try { File.Delete(f); } catch (IOException) { } } }
    private string Pbr(params (string date, string user, string item, string type, string name, string method, long dur)[] rows) { var p = MakePbrDb(rows); _files.Add(p); return p; }

    internal static string MakePbrDb(params (string date, string user, string item, string type, string name, string method, long dur)[] rows)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pbr-{Guid.NewGuid():N}.db");
        using var c = new SqliteConnection($"Data Source={path}");
        c.Open();
        using (var create = c.CreateCommand())
        {
            create.CommandText = "CREATE TABLE PlaybackActivity (DateCreated DATETIME NOT NULL, UserId TEXT, ItemId TEXT, ItemType TEXT, ItemName TEXT, PlaybackMethod TEXT, ClientName TEXT, DeviceName TEXT, PlayDuration INT)";
            create.ExecuteNonQuery();
        }
        foreach (var r in rows)
        {
            using var ins = c.CreateCommand();
            ins.CommandText = "INSERT INTO PlaybackActivity VALUES ($d,$u,$i,$t,$n,$m,'Jellyfin Web','Firefox',$p)";
            ins.Parameters.AddWithValue("$d", r.date); ins.Parameters.AddWithValue("$u", r.user); ins.Parameters.AddWithValue("$i", r.item);
            ins.Parameters.AddWithValue("$t", r.type); ins.Parameters.AddWithValue("$n", r.name); ins.Parameters.AddWithValue("$m", r.method);
            ins.Parameters.AddWithValue("$p", r.dur);
            ins.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    [Fact]
    public void ImportsRepairsAndIsIdempotent()
    {
        using var t = new TempDb();
        var store = new PlayStore(t.Db);
        var paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
        var pbr = Pbr(
            ("2026-05-08 16:31:28.5733949", "u1", "ep1", "Episode", "Station Nord - s01e04 - Le phare", "DirectPlay", -2147483648),
            ("2026-07-20 18:55:23.8036003", "u1", "ep1", "Episode", "Station Nord - s01e04 - Le phare", "Transcode (v:h264 a:aac)", 166552),
            ("2026-08-01 21:00:20.0000000", "u2", "m9", "Movie", "Atlas", "DirectStream", 5400));
        var importer = new PbrImporter(store, new FakeItems(), paris);

        var r1 = importer.Import(pbr);
        Assert.Equal(new PbrImportResult(3, 3, 0, 2), r1);
        var plays = store.Since(0, 100);
        var neg = plays.Single(p => p.Id == "pbr-1");
        Assert.Equal(0, neg.WatchedMs);
        Assert.Equal(PlaySources.ImportPbr, neg.Source);
        Assert.Equal(PlayStates.Closed, neg.State);
        Assert.Equal(new DateTimeOffset(2026, 5, 8, 14, 31, 8, 573, TimeSpan.Zero), neg.StartedAt.AddTicks(-(neg.StartedAt.Ticks % TimeSpan.TicksPerMillisecond)));
        var open = plays.Single(p => p.Id == "pbr-2");
        Assert.Equal(2_700_000, open.WatchedMs);
        Assert.Equal("Transcode", open.PlayMethod);
        Assert.Equal("h264", open.VideoCodecTo);
        Assert.Equal("aac", open.AudioCodecTo);
        Assert.Equal("Station Nord", open.SeriesName);
        var movie = plays.Single(p => p.Id == "pbr-3");
        Assert.Equal("Movie", movie.ItemType);
        Assert.Equal(5_400_000, movie.WatchedMs);
        Assert.Equal(movie.StartedAt.AddSeconds(5400), movie.EndedAt);

        var r2 = importer.Import(pbr);
        Assert.Equal(3, r2.Read);
        Assert.Equal(3, store.Since(0, 100).Count);   // still 3 rows, no duplicates
    }

    [Fact]
    public void SkipsPlaysAlreadyRecordedByThePlugin()
    {
        using var t = new TempDb();
        var store = new PlayStore(t.Db);
        var utc = TimeZoneInfo.Utc;
        var start = new DateTimeOffset(2026, 8, 1, 21, 0, 0, TimeSpan.Zero);
        store.Upsert(new[] { new PlayRecord { Id = "live", UserId = "u2", ItemId = "m9", StartedAt = start, EndedAt = start.AddMinutes(90), LastEventAt = start.AddMinutes(90), State = PlayStates.Closed } });
        var pbr = Pbr(("2026-08-01 21:00:20.0000000", "u2", "m9", "Movie", "Atlas", "DirectPlay", 5390));
        var r = new PbrImporter(store, new FakeItems(), utc).Import(pbr);
        Assert.Equal(1, r.Skipped);
        Assert.Single(store.Since(0, 100));
    }

    private static PlayRecord Only(PlayStore s) => Assert.Single(s.Since(0, 100));

    [Fact]
    public void ParsesEmptyCodecsAndNaMethod()
    {
        using var t = new TempDb(); var store = new PlayStore(t.Db);
        var pbr = Pbr(
            ("2026-08-01 21:00:20.0000000", "u1", "m1", "Audio", "Song", "Transcode (v: a:mp3)", 200),
            ("2026-08-02 21:00:20.0000000", "u1", "m2", "Movie", "Film", "na", 200));
        new PbrImporter(store, new FakeItems(), TimeZoneInfo.Utc).Import(pbr);
        var plays = store.Since(0, 10);
        var song = plays.Single(p => p.ItemId == "m1");
        Assert.Equal("Transcode", song.PlayMethod); Assert.Null(song.VideoCodecTo); Assert.Equal("mp3", song.AudioCodecTo);
        Assert.Null(plays.Single(p => p.ItemId == "m2").PlayMethod);
    }

    [Fact]
    public void BadRowsAreSkippedAndTheRestImported()
    {
        using var t = new TempDb(); var store = new PlayStore(t.Db);
        var pbr = Pbr(
            ("not a date", "u1", "m1", "Movie", "Bad", "DirectPlay", 100),
            ("2026-08-02 21:00:20.0000000", "u1", "m2", "Movie", "Good", "DirectPlay", 100));
        var r = new PbrImporter(store, new FakeItems(), TimeZoneInfo.Utc).Import(pbr);
        Assert.Equal(2, r.Read); Assert.Equal(1, r.Imported); Assert.Equal(1, r.Skipped);
    }

    [Fact]
    public void ParsesTimestampVariantsAndOffsets()
    {
        using var t = new TempDb(); var store = new PlayStore(t.Db);
        var paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
        var pbr = Pbr(
            ("2026-08-02 21:00:20", "u1", "a", "Movie", "A", "DirectPlay", 10),
            ("2026-08-03T21:00:20Z", "u1", "b", "Movie", "B", "DirectPlay", 10),
            ("2026-08-04 21:00:20+05:00", "u1", "c", "Movie", "C", "DirectPlay", 10));
        new PbrImporter(store, new FakeItems(), paris).Import(pbr);
        var p = store.Since(0, 10).ToDictionary(x => x.ItemId);
        Assert.Equal(new DateTimeOffset(2026, 8, 2, 19, 0, 0, TimeSpan.Zero), p["a"].StartedAt);       // Paris summer time, local string
        Assert.Equal(new DateTimeOffset(2026, 8, 3, 21, 0, 0, TimeSpan.Zero), p["b"].StartedAt);       // Z: converted directly
        Assert.Equal(new DateTimeOffset(2026, 8, 4, 16, 0, 0, TimeSpan.Zero), p["c"].StartedAt);       // offset: converted directly
    }

    [Fact]
    public void IdsAreNormalisedToNFormat()
    {
        using var t = new TempDb(); var store = new PlayStore(t.Db);
        var user = Guid.NewGuid(); var item = Guid.NewGuid();
        var pbr = Pbr(("2026-08-02 21:00:20.0000000", user.ToString("D"), item.ToString("D"), "Movie", "A", "DirectPlay", 10));
        new PbrImporter(store, new FakeItems(), TimeZoneInfo.Utc).Import(pbr);
        var p = Only(store);
        Assert.Equal(user.ToString("N"), p.UserId); Assert.Equal(item.ToString("N"), p.ItemId);
    }

    [Fact]
    public void ReimportDoesNotBumpVersionsOfExistingRows()
    {
        using var t = new TempDb(); var store = new PlayStore(t.Db);
        var pbr = Pbr(("2026-08-02 21:00:20.0000000", "u1", "m1", "Movie", "A", "DirectPlay", 10), ("2026-08-03 21:00:20.0000000", "u1", "m2", "Movie", "B", "DirectPlay", 10));
        var importer = new PbrImporter(store, new FakeItems(), TimeZoneInfo.Utc);
        importer.Import(pbr);
        var cursor = store.Cursor();
        var r = importer.Import(pbr);
        Assert.Equal(cursor, store.Cursor());
        Assert.Equal(2, r.Skipped);
    }

    [Fact]
    public void ItemLookupIsCachedPerImport()
    {
        using var t = new TempDb(); var store = new PlayStore(t.Db);
        var items = new CountingItems();
        var pbr = Pbr(("2026-08-02 21:00:20.0000000", "u1", "m1", "Movie", "A", "DirectPlay", 10), ("2026-08-03 21:00:20.0000000", "u2", "m1", "Movie", "A", "DirectPlay", 10));
        new PbrImporter(store, items, TimeZoneInfo.Utc).Import(pbr);
        Assert.Equal(1, items.Calls);
    }

    private sealed class CountingItems : IItemLookup
    {
        public int Calls;
        public ItemInfo? Find(string itemId) { Calls++; return null; }
    }
}
