using Jellyfin.Plugin.Jellystics.Models;
using Jellyfin.Plugin.Jellystics.Storage;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class AnnexStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VersionCounterIsSharedAcrossTables()
    {
        using var t = new TempDb();
        new PlayStore(t.Db).Upsert(new[] { new PlayRecord { Id = "p", UserId = "u", ItemId = "i", StartedAt = T0, LastEventAt = T0 } });
        var annex = new AnnexStore(t.Db);
        annex.UpsertLibrary(new[] { new LibraryEventRecord { Id = "add-i", Kind = "added", At = T0, ItemId = "i" } });
        Assert.Equal(2, Assert.Single(annex.LibrarySince(0, 10)).Version);
    }

    [Fact]
    public void AddedEventsAreUniquePerItem()
    {
        using var t = new TempDb();
        var annex = new AnnexStore(t.Db);
        var ev = new LibraryEventRecord { Id = "add-i", Kind = "added", At = T0, ItemId = "i" };
        annex.UpsertLibrary(new[] { ev });
        annex.UpsertLibrary(new[] { ev });
        Assert.Equal(1, annex.CountLibrary());
        Assert.Equal(1, new PlayStore(t.Db).Cursor());   // duplicate did not burn a version
    }

    [Fact]
    public void UserDataAndLoginsRoundTrip()
    {
        using var t = new TempDb();
        var annex = new AnnexStore(t.Db);
        annex.UpsertUserData(new[] { new UserDataEventRecord { Id = "ud1", At = T0, UserId = "u", ItemId = "i", Kind = "played", Reason = "TogglePlayed" } });
        annex.UpsertLogins(new[] { new LoginRecord { Id = "l1", At = T0, UserName = "léa", Success = false, RemoteEndPoint = "82.64.10.1" } });
        Assert.Equal("TogglePlayed", Assert.Single(annex.UserDataSince(0, 10)).Reason);
        var login = Assert.Single(annex.LoginsSince(0, 10));
        Assert.False(login.Success);
        Assert.Equal("léa", login.UserName);
    }

    [Fact]
    public void MetaFlagsRoundTrip()
    {
        using var t = new TempDb();
        var annex = new AnnexStore(t.Db);
        Assert.Null(annex.GetMeta("catalog_snapshot_done"));
        annex.SetMeta("catalog_snapshot_done", "1");
        annex.SetMeta("catalog_snapshot_done", "2");
        Assert.Equal("2", annex.GetMeta("catalog_snapshot_done"));
    }

    [Fact]
    public void ReAddingARemovedItemWritesANewAddedEvent()
    {
        using var t = new TempDb();
        var annex = new AnnexStore(t.Db);
        var at = new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero);
        annex.UpsertLibrary(new[] { new LibraryEventRecord { Id = "add-i", Kind = "added", At = at, ItemId = "i" } });
        annex.UpsertLibrary(new[] { new LibraryEventRecord { Id = "rm-i-1", Kind = "removed", At = at.AddDays(1), ItemId = "i" } });
        annex.UpsertLibrary(new[] { new LibraryEventRecord { Id = "add-i", Kind = "added", At = at.AddDays(2), ItemId = "i" } });
        var kinds = annex.LibrarySince(0, 10).Select(e => e.Kind).ToArray();
        Assert.Equal(new[] { "added", "removed", "added" }, kinds);
        // and a duplicate add right after is ignored again
        annex.UpsertLibrary(new[] { new LibraryEventRecord { Id = "add-i", Kind = "added", At = at.AddDays(3), ItemId = "i" } });
        Assert.Equal(3, annex.LibrarySince(0, 10).Count);
    }

    [Fact]
    public void PresentRowsAreUniquePerItemAndKeepDateCreated()
    {
        using var t = new TempDb();
        var annex = new AnnexStore(t.Db);
        var created = T0.AddYears(-2);
        LibraryEventRecord Rec() => new() { Id = "present-i", Kind = "present", At = T0, ItemId = "i", DateCreated = created };
        annex.UpsertLibrary(new[] { Rec() });
        annex.UpsertLibrary(new[] { Rec() });   // snapshot rerun
        var row = Assert.Single(annex.LibrarySince(0, 10));
        Assert.Equal("present", row.Kind);
        Assert.Equal(created, row.DateCreated);
        Assert.Equal(T0, row.At);
    }

    [Theory]
    [InlineData("present-i")]
    [InlineData("add-i")]   // id of a row migrated from v1
    public void LiveAddSupersedesAPresentRowInPlace(string presentId)
    {
        using var t = new TempDb();
        var annex = new AnnexStore(t.Db);
        var created = T0.AddYears(-1);
        annex.UpsertLibrary(new[] { new LibraryEventRecord { Id = presentId, Kind = "present", At = T0, ItemId = "i", DateCreated = created } });
        var v1 = Assert.Single(annex.LibrarySince(0, 10)).Version;
        annex.UpsertLibrary(new[] { new LibraryEventRecord { Id = "add-i", Kind = "added", At = T0.AddDays(3), ItemId = "i", DateCreated = created } });
        var row = Assert.Single(annex.LibrarySince(0, 10));
        Assert.Equal(presentId, row.Id);
        Assert.Equal("added", row.Kind);
        Assert.Equal(T0.AddDays(3), row.At);
        Assert.Equal(created, row.DateCreated);
        Assert.True(row.Version > v1);
        annex.UpsertLibrary(new[] { new LibraryEventRecord { Id = "add-i", Kind = "added", At = T0.AddDays(4), ItemId = "i" } });   // already added: ignored
        Assert.Equal(T0.AddDays(3), Assert.Single(annex.LibrarySince(0, 10)).At);
    }

    [Fact]
    public void PresentIsSkippedWhenALiveAddAlreadyExists()
    {
        using var t = new TempDb();
        var annex = new AnnexStore(t.Db);
        annex.UpsertLibrary(new[] { new LibraryEventRecord { Id = "add-i", Kind = "added", At = T0, ItemId = "i", DateCreated = T0.AddDays(-1) } });
        annex.UpsertLibrary(new[] { new LibraryEventRecord { Id = "present-i", Kind = "present", At = T0.AddHours(1), ItemId = "i" } });
        Assert.Equal("added", Assert.Single(annex.LibrarySince(0, 10)).Kind);
    }

    [Fact]
    public void MigrationV2RewritesExistingAddedRowsAsPresentAndBumpsTheirVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mig-{Guid.NewGuid():N}.db");
        try
        {
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = Migrations.Scripts[0] +
                    "CREATE TABLE schema_version (version INTEGER NOT NULL); INSERT INTO schema_version VALUES (1);" +
                    "UPDATE version_seq SET value = 4;" +
                    "INSERT INTO LibraryEvents (Id,Kind,At,ItemId,Version) VALUES ('add-a','added','2026-01-01T00:00:00.0000000Z','a',1),('add-b','added','2026-01-02T00:00:00.0000000Z','b',2),('rm-a-1','removed','2026-02-01T00:00:00.0000000Z','a',3),('add-a-20260301000000','added','2026-03-01T00:00:00.0000000Z','a',4);";
                cmd.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var db = new JellysticsDb(path);
            db.Migrate();
            db.Migrate();   // second run is a no-op
            var annex = new AnnexStore(db);
            var rows = annex.LibrarySince(0, 10);
            // only the FIRST added row per item becomes present; the re-add row stays added (DateCreated = At)
            Assert.Equal(new[] { "removed", "added", "present", "present" }, rows.Select(r => r.Kind).ToArray());
            var a = rows.Single(r => r.Id == "add-a");
            Assert.Equal(a.At, a.DateCreated);
            var readd = rows.Single(r => r.Id == "add-a-20260301000000");
            Assert.Equal(readd.At, readd.DateCreated);
            Assert.Equal(4, rows.Count);
            Assert.Equal(6,new PlayStore(db).Cursor());
            Assert.Equal(rows.Select(r => r.Version).Distinct().Count(), rows.Count);
            // Plays.DurationRepaired exists after migration
            new PlayStore(db).Upsert(new[] { new PlayRecord { Id = "p", UserId = "u", ItemId = "i", StartedAt = T0, LastEventAt = T0, DurationRepaired = false } });
            Assert.False(new PlayStore(db).Get("p")!.DurationRepaired);
            // a re-add after a removal on a migrated database writes a new added row
            annex.UpsertLibrary(new[] { new LibraryEventRecord { Id = "add-a", Kind = "added", At = T0, ItemId = "a" } });
            annex.UpsertLibrary(new[] { new LibraryEventRecord { Id = "rm-a-2", Kind = "removed", At = T0.AddDays(1), ItemId = "a" } });
            annex.UpsertLibrary(new[] { new LibraryEventRecord { Id = "add-a", Kind = "added", At = T0.AddDays(2), ItemId = "a" } });
            Assert.Equal("added", annex.LibrarySince(0, 20).Last().Kind);
            Assert.Equal(T0.AddDays(2), annex.LibrarySince(0, 20).Last().At);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); }
    }
}
