using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Jellystics.Api;
using Jellyfin.Plugin.Jellystics.Import;
using Jellyfin.Plugin.Jellystics.Me;
using Jellyfin.Plugin.Jellystics.Models;
using Jellyfin.Plugin.Jellystics.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class SyncControllerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 19, 0, 0, TimeSpan.Zero);

    private sealed class Env : IDisposable
    {
        public TempDb Db = new();
        public PlayStore Plays;
        public AnnexStore Annex;
        public WriteQueue Queue = new(NullLogger<WriteQueue>.Instance);
        public SyncController Controller;

        public Env()
        {
            Plays = new PlayStore(Db.Db); Annex = new AnnexStore(Db.Db);
            Queue.StartAsync(CancellationToken.None).Wait();
            Controller = new SyncController(Plays, Annex, Queue, new MeLink(new FakeMeSettings(), new FakeHttp((_, _) => throw new HttpRequestException("offline"))));
        }

        public void Dispose() { Queue.StopAsync(CancellationToken.None).Wait(); Db.Dispose(); }

        public void Body(string json)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
            Controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        }
    }

    private static PlayRecord Play(string id, string source = PlaySources.Plugin) => new()
    {
        Id = id, UserId = "u1", ItemId = "i1", ItemType = "Movie", StartedAt = T0, EndedAt = T0.AddMinutes(5), LastEventAt = T0.AddMinutes(5),
        State = PlayStates.Closed, Source = source, WatchedMs = 300000, StreamChanges = "[]", TranscodeReasons = "[\"A\"]",
        IsVideoDirect = true, SourceWidth = 1920,
    };

    private static JsonDocument Json(IActionResult r, int status = 200)
    {
        var c = Assert.IsType<ContentResult>(r);
        Assert.Equal(status, c.StatusCode ?? 200);
        Assert.Equal("application/json", c.ContentType);
        return JsonDocument.Parse(c.Content!);
    }

    [Fact]
    public void ControllerRequiresElevation()
    {
        var a = typeof(SyncController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal("RequiresElevation", a?.Policy);
        Assert.NotNull(typeof(SyncController).GetCustomAttribute<ApiControllerAttribute>());
    }

    [Fact]
    public void InfoOnAnEmptyDb()
    {
        using var e = new Env();
        var root = Json(e.Controller.Info()).RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, root.GetProperty("cursor").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("firstPlayAt").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("pluginVersion").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("serverTimeZone").ValueKind);
        var counts = root.GetProperty("counts");
        foreach (var n in new[] { "plays", "libraryEvents", "userDataEvents", "logins" }) Assert.Equal(0, counts.GetProperty(n).GetInt32());
    }

    [Fact]
    public void InfoDatabaseIdIsStableAndChangesWithTheDatabase()
    {
        using var e = new Env();
        var id = Json(e.Controller.Info()).RootElement.GetProperty("databaseId").GetString();
        Assert.Matches("^[0-9a-f]{32}$", id);
        e.Db.Db.Migrate();   // restart on the same database
        Assert.Equal(id, Json(e.Controller.Info()).RootElement.GetProperty("databaseId").GetString());
        using var other = new Env();   // reinstall / lost database
        Assert.NotEqual(id, Json(other.Controller.Info()).RootElement.GetProperty("databaseId").GetString());
    }

    [Fact]
    public void InfoCursorIsTheGlobalMaxVersionAcrossTables()
    {
        using var e = new Env();
        e.Plays.Upsert(new[] { Play("a"), Play("b") });
        e.Annex.UpsertUserData(new[] { new UserDataEventRecord { Id = "x", At = T0, UserId = "u", ItemId = "i", Kind = "played" } });
        e.Annex.UpsertLogins(new[] { new LoginRecord { Id = "l", At = T0, Success = true } });
        var root = Json(e.Controller.Info()).RootElement;
        Assert.Equal(4, root.GetProperty("cursor").GetInt64());
        Assert.Equal(2, root.GetProperty("counts").GetProperty("plays").GetInt32());
        Assert.Equal(1, root.GetProperty("counts").GetProperty("userDataEvents").GetInt32());
        Assert.Equal(1, root.GetProperty("counts").GetProperty("logins").GetInt32());
        Assert.Equal("2026-11-20T19:00:00.0000000Z", root.GetProperty("firstPlayAt").GetString());
    }

    private static readonly string[] PlayFields =
    {
        "id", "playSessionId", "userId", "userName", "itemId", "itemName", "itemType", "seriesId", "seriesName", "seasonId", "albumId", "albumName",
        "parentIndexNumber", "indexNumber", "mediaSourceId", "libraryId", "startedAt", "endedAt", "watchedMs", "pausedMs", "pauseCount", "seekCount",
        "skippedMs", "positionStartTicks", "positionEndTicks", "runTimeTicks", "playedToCompletion", "deviceId", "deviceName", "client",
        "applicationVersion", "remoteEndPoint", "isRemote", "playMethod", "audioStreamIndex", "audioLanguage", "audioCodec", "subtitleStreamIndex",
        "subtitleLanguage", "streamChanges", "videoCodecFrom", "videoCodecTo", "audioCodecFrom", "audioCodecTo", "width", "height", "bitrate", "framerate",
        "hwAccel", "transcodeReasons", "videoRange", "isVideoDirect", "isAudioDirect", "sourceWidth", "sourceHeight", "videoRangeType", "state",
        "lastEventAt", "source", "durationRepaired", "version",
    };

    [Fact]
    public void PlaysUseExactlyTheContractPropertyNamesAndFormats()
    {
        using var e = new Env();
        e.Plays.Upsert(new[] { Play("a") });
        var root = Json(e.Controller.Plays(0, null)).RootElement;
        var item = root.GetProperty("items")[0];
        Assert.Equal(PlayFields.Order(), item.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("2026-11-20T19:00:00.0000000Z", item.GetProperty("startedAt").GetString());
        Assert.Equal(JsonValueKind.String, item.GetProperty("streamChanges").ValueKind);
        Assert.Equal("[\"A\"]", item.GetProperty("transcodeReasons").GetString());
        Assert.True(item.GetProperty("isVideoDirect").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("isAudioDirect").ValueKind);
        Assert.Equal(1, item.GetProperty("version").GetInt64());
        Assert.False(root.GetProperty("hasMore").GetBoolean());
        Assert.Equal(1, root.GetProperty("next").GetInt64());
    }

    [Fact]
    public void PaginationNextIsLastVersionAndHasMoreAdvances()
    {
        using var e = new Env();
        e.Plays.Upsert(Enumerable.Range(1, 5).Select(i => Play("p" + i)).ToArray());
        var collected = new List<string>();
        long since = 0; int pages = 0; bool more = true;
        while (more)
        {
            var root = Json(e.Controller.Plays(since, 2)).RootElement;
            var items = root.GetProperty("items").EnumerateArray().ToList();
            collected.AddRange(items.Select(i => i.GetProperty("id").GetString()!));
            var next = root.GetProperty("next").GetInt64();
            Assert.Equal(items[^1].GetProperty("version").GetInt64(), next);
            Assert.True(next > since);
            more = root.GetProperty("hasMore").GetBoolean();
            since = next; pages++;
        }
        Assert.Equal(new[] { "p1", "p2", "p3", "p4", "p5" }, collected);
        Assert.Equal(3, pages);
    }

    [Fact]
    public void EmptyPageKeepsSinceAsNextNeverZero()
    {
        using var e = new Env();
        e.Plays.Upsert(new[] { Play("a") });
        var root = Json(e.Controller.Plays(1, null)).RootElement;
        Assert.Empty(root.GetProperty("items").EnumerateArray());
        Assert.Equal(1, root.GetProperty("next").GetInt64());
        Assert.False(root.GetProperty("hasMore").GetBoolean());
        Assert.Equal(7, Json(e.Controller.Plays(7, null)).RootElement.GetProperty("next").GetInt64());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void InvalidLimitIs400(int limit)
    {
        using var e = new Env();
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(e.Controller.Plays(0, limit)).StatusCode);
        Assert.IsType<BadRequestObjectResult>(e.Controller.Logins(0, limit));
    }

    [Fact]
    public void NegativeSinceIs400()
    {
        using var e = new Env();
        Assert.IsType<BadRequestObjectResult>(e.Controller.Plays(-1, null));
    }

    [Fact]
    public void LimitBoundsAreAccepted()
    {
        using var e = new Env();
        Json(e.Controller.Plays(0, 1)); Json(e.Controller.Plays(0, 1000));
    }

    [Fact]
    public void AnnexEndpointsUseCamelCaseNames()
    {
        using var e = new Env();
        e.Annex.UpsertLibrary(new[] { new LibraryEventRecord { Id = "add-i", Kind = "added", At = T0, ItemId = "i", ItemType = "Movie", RunTimeTicks = 5, SizeBytes = 9 } });
        e.Annex.UpsertUserData(new[] { new UserDataEventRecord { Id = "u", At = T0, UserId = "u", ItemId = "i", Kind = "played", Reason = "TogglePlayed" } });
        e.Annex.UpsertLogins(new[] { new LoginRecord { Id = "l", At = T0, UserId = "u", Success = true } });
        string[] Names(IActionResult r) => Json(r).RootElement.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(new[] { "albumId", "at", "dateCreated", "id", "itemId", "itemName", "itemType", "kind", "libraryId", "runTimeTicks", "seriesId", "sizeBytes", "version" }, Names(e.Controller.LibraryEvents(0, null)));
        Assert.Equal(new[] { "at", "id", "itemId", "kind", "reason", "userId", "version" }, Names(e.Controller.UserDataEvents(0, null)));
        Assert.Equal(new[] { "applicationVersion", "at", "client", "deviceId", "deviceName", "id", "remoteEndPoint", "success", "userId", "userName", "version" }, Names(e.Controller.Logins(0, null)));
    }

    [Fact]
    public async Task ImportInsertsAsImportJellysticsAndIsIdempotent()
    {
        using var e = new Env();
        var body = """{"items":[{"id":"jf-1","userId":"u1","itemId":"i1","itemType":"Movie","startedAt":"2026-01-01T10:00:00Z","endedAt":"2026-01-01T11:00:00Z","watchedMs":3600000,"state":"closed","source":"plugin"}]}""";
        e.Body(body);
        var res = Json(await e.Controller.ImportPlays()).RootElement;
        Assert.Equal(1, res.GetProperty("imported").GetInt32());
        var p = e.Plays.Get("jf-1")!;
        Assert.Equal(PlaySources.ImportJellystics, p.Source);
        Assert.Equal(3600000, p.WatchedMs);
        var cursor = e.Plays.Cursor();
        e.Body(body);
        Assert.Equal(0, Json(await e.Controller.ImportPlays()).RootElement.GetProperty("imported").GetInt32());
        Assert.Equal(cursor, e.Plays.Cursor());
    }

    [Fact]
    public async Task ImportNeverOverwritesAPluginRow()
    {
        using var e = new Env();
        e.Plays.Upsert(new[] { Play("live") });
        e.Body("""{"items":[{"id":"live","userId":"u1","itemId":"i1","startedAt":"2026-01-01T10:00:00Z","watchedMs":1}]}""");
        var res = Json(await e.Controller.ImportPlays()).RootElement;
        Assert.Equal(1, res.GetProperty("skipped").GetInt32());
        var p = e.Plays.Get("live")!;
        Assert.Equal(PlaySources.Plugin, p.Source);
        Assert.Equal(300000, p.WatchedMs);
    }

    [Fact]
    public async Task ImportRejectsMoreThan500AndBadBodies()
    {
        using var e = new Env();
        var many = string.Join(",", Enumerable.Range(0, 501).Select(i => $$"""{"id":"x{{i}}","userId":"u","itemId":"i","startedAt":"2026-01-01T10:00:00Z"}"""));
        e.Body("{\"items\":[" + many + "]}");
        Assert.IsType<BadRequestObjectResult>(await e.Controller.ImportPlays());
        e.Body("not json");
        Assert.IsType<BadRequestObjectResult>(await e.Controller.ImportPlays());
        e.Body("{}");
        Assert.IsType<BadRequestObjectResult>(await e.Controller.ImportPlays());
        Assert.Empty(e.Plays.Since(0, 10));
    }

    [Fact]
    public async Task ImportWithAMalformedDateIsA400Not500()
    {
        using var e = new Env();
        e.Body("""{"items":[{"id":"x","userId":"u","itemId":"i","startedAt":"not-a-date"}]}""");
        Assert.IsType<BadRequestObjectResult>(await e.Controller.ImportPlays());
        e.Body("""{"items":[{"id":"x","userId":"u","itemId":"i","startedAt":12}]}""");
        Assert.IsType<BadRequestObjectResult>(await e.Controller.ImportPlays());
    }

    [Theory]
    [InlineData("""{"items":[{"id":"x","userId":"u","itemId":"i","startedAt":"2026-11-20T19:00:00Z","endedAt":"nope"}]}""")]
    [InlineData("""{"items":[{"id":"x","userId":"u","itemId":"i","startedAt":"2026-11-20T19:00:00Z","lastEventAt":"31/31/2026"}]}""")]
    [InlineData("""{"items":[{"id":"x","userId":"u","itemId":"i","startedAt":null}]}""")]
    [InlineData("""{"items":[{"id":"ok","userId":"u","itemId":"i","startedAt":"2026-11-20T19:00:00Z"},{"id":"x","userId":"u","itemId":"i","startedAt":"garbage"}]}""")]
    public async Task ImportWithOtherBadDatesIsA400AndImportsNothing(string body)
    {
        using var e = new Env();
        e.Body(body);
        Assert.IsType<BadRequestObjectResult>(await e.Controller.ImportPlays());
        Assert.Empty(e.Plays.Since(0, 10));
    }

    [Fact]
    public async Task ImportNormalisesEveryIdField()
    {
        using var e = new Env();
        var g = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
        e.Body($$"""{"items":[{"id":"x","userId":"{{g[0]:D}}","itemId":"{{g[1]:D}}","seriesId":"{{g[2]:B}}","seasonId":"{{g[3]:D}}","albumId":"{{g[4]:D}}","libraryId":"{{g[5]:D}}","mediaSourceId":"{{g[6]:D}}","startedAt":"2026-11-20T19:00:00Z","durationRepaired":true}]}""");
        await e.Controller.ImportPlays();
        var p = Assert.Single(e.Plays.Since(0, 10));
        Assert.Equal(new[] { p.UserId, p.ItemId, p.SeriesId, p.SeasonId, p.AlbumId, p.LibraryId, p.MediaSourceId }, g.Select(x => x.ToString("N")).ToArray());
        Assert.True(p.DurationRepaired);   // stored as sent on import-jellystics rows
    }

    [Fact]
    public void NewFieldsAreSerialisedCamelCase()
    {
        using var e = new Env();
        e.Plays.Upsert(new[] { new PlayRecord { Id = "p", UserId = "u", ItemId = "i", StartedAt = T0, LastEventAt = T0, DurationRepaired = true }, new PlayRecord { Id = "q", UserId = "u", ItemId = "i", StartedAt = T0, LastEventAt = T0 } });
        var plays = Json(e.Controller.Plays()).RootElement.GetProperty("items");
        Assert.True(plays[0].GetProperty("durationRepaired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, plays[1].GetProperty("durationRepaired").ValueKind);
        e.Annex.UpsertLibrary(new[]
        {
            new LibraryEventRecord { Id = "present-i", Kind = "present", At = T0, ItemId = "i", DateCreated = T0.AddDays(-9) },
            new LibraryEventRecord { Id = "rm-i", Kind = "removed", At = T0, ItemId = "i" },
        });
        var ev = Json(e.Controller.LibraryEvents()).RootElement.GetProperty("items");
        Assert.Equal("present", ev[0].GetProperty("kind").GetString());
        Assert.Equal("2026-11-11T19:00:00.0000000Z", ev[0].GetProperty("dateCreated").GetString());
        Assert.Equal(JsonValueKind.Null, ev[1].GetProperty("dateCreated").ValueKind);
    }

    [Fact]
    public async Task ImportSkipsInvalidRowsAndKeepsTheRest()
    {
        using var e = new Env();
        e.Body("""{"items":[{"id":"","userId":"u","itemId":"i","startedAt":"2026-01-01T10:00:00Z"},{"id":"ok","userId":"u","itemId":"i","startedAt":"2026-01-01T10:00:00Z"}]}""");
        var res = Json(await e.Controller.ImportPlays()).RootElement;
        Assert.Equal(1, res.GetProperty("imported").GetInt32());
        Assert.Equal(1, res.GetProperty("rejected").GetInt32());
    }
}
