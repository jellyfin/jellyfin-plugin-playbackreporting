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
}
