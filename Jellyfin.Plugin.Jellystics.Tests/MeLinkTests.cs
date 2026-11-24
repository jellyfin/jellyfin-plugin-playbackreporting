using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Jellystics.Api;
using Jellyfin.Plugin.Jellystics.Me;
using Jellyfin.Plugin.Jellystics.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class MeLinkTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";

    private sealed class Env : IDisposable
    {
        public TempDb Db = new();
        public WriteQueue Queue = new(NullLogger<WriteQueue>.Instance);
        public FakeMeSettings Settings = new();
        public FakeHttp Http;
        public MeLink Link;
        public SyncController Controller;

        public Env(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
        {
            Http = new FakeHttp(respond ?? (_ => FakeHttp.Text(200, "{\"ok\":true}")));
            Link = new MeLink(Settings, Http);
            Controller = new SyncController(new PlayStore(Db.Db), new AnnexStore(Db.Db), Queue, Link);
        }

        public void Dispose() => Db.Dispose();

        public IActionResult PostLink(string json)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
            Controller.ControllerContext = new ControllerContext { HttpContext = ctx };
            return Controller.Link().GetAwaiter().GetResult();
        }

        public JsonElement Info() => JsonDocument.Parse(Assert.IsType<ContentResult>(Controller.Info()).Content!).RootElement;
    }

    [Fact]
    public void LinkIsAnAdminPostOnTheSyncController()
    {
        var m = typeof(SyncController).GetMethod(nameof(SyncController.Link))!;
        Assert.Equal("Link", m.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Null(m.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());
        Assert.Equal("RequiresElevation", typeof(SyncController).GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()?.Policy);
    }

    [Fact]
    public void LinkPersistsAndChecksHealthWithTheSecretOnly()
    {
        using var e = new Env();
        var r = Assert.IsType<ContentResult>(e.PostLink("{\"url\":\"http://10.0.0.5:3000/\",\"secret\":\"" + Secret + "\"}"));
        var root = JsonDocument.Parse(r.Content!).RootElement;
        Assert.True(root.GetProperty("linked").GetBoolean());
        Assert.True(root.GetProperty("reachable").GetBoolean());
        Assert.Equal("http://10.0.0.5:3000", e.Settings.Url);
        Assert.Equal(Secret, e.Settings.Secret);
        var health = Assert.Single(e.Http.Requests);
        Assert.Equal("http://10.0.0.5:3000/api/me/health", health.Uri.ToString());
        Assert.Equal(Secret, health.Headers[MeLink.SecretHeader]);
        Assert.False(health.Headers.ContainsKey(MeLink.UserHeader));
    }

    [Fact]
    public void LinkKeepsABasePathAndReportsUnreachable()
    {
        using var e = new Env(_ => FakeHttp.Text(401, "{}"));
        var root = JsonDocument.Parse(Assert.IsType<ContentResult>(e.PostLink("{\"url\":\"https://stats.lan/jellystics\",\"secret\":\"" + Secret + "\"}")).Content!).RootElement;
        Assert.True(root.GetProperty("linked").GetBoolean());
        Assert.False(root.GetProperty("reachable").GetBoolean());
        Assert.Equal("https://stats.lan/jellystics/api/me/health", e.Http.Requests[0].Uri.ToString());
    }

    [Fact]
    public void LinkReportsUnreachableOnNetworkError()
    {
        using var e = new Env(_ => throw new HttpRequestException("refused"));
        var root = JsonDocument.Parse(Assert.IsType<ContentResult>(e.PostLink("{\"url\":\"http://x:1\",\"secret\":\"" + Secret + "\"}")).Content!).RootElement;
        Assert.False(root.GetProperty("reachable").GetBoolean());
        Assert.False(e.Link.Reachable);
    }

    [Theory]
    [InlineData("{\"url\":\"ftp://host/\",\"secret\":\"" + Secret + "\"}")]
    [InlineData("{\"url\":\"/relative\",\"secret\":\"" + Secret + "\"}")]
    [InlineData("{\"url\":\"http://host/?a=1\",\"secret\":\"" + Secret + "\"}")]
    [InlineData("{\"url\":\"http://user:pw@host/\",\"secret\":\"" + Secret + "\"}")]
    [InlineData("{\"url\":\"\",\"secret\":\"" + Secret + "\"}")]
    [InlineData("{\"secret\":\"" + Secret + "\"}")]
    [InlineData("{\"url\":\"http://host\",\"secret\":\"short\"}")]
    [InlineData("{\"url\":\"http://host\",\"secret\":\"has a space in the middle!!\"}")]
    [InlineData("{\"url\":\"http://host\"}")]
    [InlineData("not json")]
    [InlineData("null")]
    public void InvalidLinksAreRejectedAndNotPersisted(string body)
    {
        using var e = new Env();
        var r = e.PostLink(body);
        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(r).StatusCode);
        Assert.Equal(0, e.Settings.Saves);
        Assert.Empty(e.Http.Requests);
    }

    [Fact]
    public void OverrideWinsOverThePushedUrl()
    {
        using var e = new Env();
        e.Settings.UrlOverride = "http://override:8080/";
        e.PostLink("{\"url\":\"http://pushed:3000\",\"secret\":\"" + Secret + "\"}");
        Assert.Equal("http://pushed:3000", e.Settings.Url);   // the pushed url is still stored
        Assert.Equal("http://override:8080", e.Link.EffectiveUrl);
        Assert.Equal("http://override:8080/api/me/health", e.Http.Requests[0].Uri.ToString());
    }

    [Fact]
    public void InvalidOverrideIsIgnoredAndOverrideAloneIsNotALink()
    {
        var s = new FakeMeSettings { UrlOverride = "not a url", Url = "http://pushed:3000", Secret = Secret };
        var link = new MeLink(s, new FakeHttp(_ => FakeHttp.Text(200, "")));
        Assert.Equal("http://pushed:3000", link.EffectiveUrl);
        var noSecret = new MeLink(new FakeMeSettings { UrlOverride = "http://o:1" }, new FakeHttp(_ => FakeHttp.Text(200, "")));
        Assert.False(noSecret.IsLinked);
    }

    [Fact]
    public void ReachabilityIsForgottenWhenTheEffectiveUrlChanges()
    {
        var s = new FakeMeSettings { Url = "http://a:1", Secret = Secret };
        var link = new MeLink(s, new FakeHttp(_ => FakeHttp.Text(200, "")));
        Assert.Null(link.Reachable);
        Assert.True(link.CheckHealthAsync().GetAwaiter().GetResult());
        Assert.True(link.Reachable);
        s.UrlOverride = "http://b:2";
        Assert.Null(link.Reachable);
    }

    [Fact]
    public void InfoReportsTheLinkWithoutChangingTheSchemaVersion()
    {
        using var e = new Env();
        var before = e.Info();
        Assert.Equal(1, before.GetProperty("schemaVersion").GetInt32());
        Assert.False(before.GetProperty("meLinked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("meReachable").ValueKind);
        foreach (var n in new[] { "pluginVersion", "cursor", "databaseId", "firstPlayAt", "counts", "serverTimeZone" }) Assert.True(before.TryGetProperty(n, out _), n);

        e.PostLink("{\"url\":\"http://j:3000\",\"secret\":\"" + Secret + "\"}");
        var after = e.Info();
        Assert.True(after.GetProperty("meLinked").GetBoolean());
        Assert.True(after.GetProperty("meReachable").GetBoolean());
    }

    [Fact]
    public void InfoStartsABackgroundHealthCheckWhenReachabilityIsUnknown()
    {
        using var e = new Env();
        e.Settings.Url = "http://j:3000"; e.Settings.Secret = Secret;   // linked before a restart
        Assert.Null(e.Link.Reachable);
        e.Info();   // may already answer true if the check is very fast
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (e.Link.Reachable is null && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(e.Info().GetProperty("meReachable").GetBoolean());
    }
}
