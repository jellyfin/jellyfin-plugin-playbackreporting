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
        Assert.True(annex.LibraryAddedExists("i"));
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
                    "UPDATE version_seq SET value = 3;" +
                    "INSERT INTO LibraryEvents (Id,Kind,At,ItemId,Version) VALUES ('add-a','added','2026-01-01T00:00:00.0000000Z','a',1),('add-b','added','2026-01-02T00:00:00.0000000Z','b',2),('rm-a-1','removed','2026-02-01T00:00:00.0000000Z','a',3);";
                cmd.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var db = new JellysticsDb(path);
            db.Migrate();
            var rows = new AnnexStore(db).LibrarySince(0, 10);
            Assert.Equal(new[] { "removed", "present", "present" }, rows.Select(r => r.Kind).ToArray());
            var a = rows.Single(r => r.ItemId == "a" && r.Kind == "present");
            Assert.Equal(a.At, a.DateCreated);
            Assert.Equal(new long[] { 3, 4, 5 }, rows.Select(r => r.Version).ToArray());
            Assert.Equal(5, new PlayStore(db).Cursor());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); }
    }
}
