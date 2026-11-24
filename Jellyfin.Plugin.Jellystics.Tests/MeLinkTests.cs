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
    public void LinkKeepsABasePathAndReportsASecretMismatchOn401()
    {
        using var e = new Env(_ => FakeHttp.Text(401, "{}"));
        var root = JsonDocument.Parse(Assert.IsType<ContentResult>(e.PostLink("{\"url\":\"https://stats.lan/jellystics\",\"secret\":\"" + Secret + "\"}")).Content!).RootElement;
        Assert.True(root.GetProperty("linked").GetBoolean());
        Assert.False(root.GetProperty("reachable").GetBoolean());
        Assert.Equal("secretMismatch", root.GetProperty("error").GetString());
        Assert.Equal("https://stats.lan/jellystics/api/me/health", e.Http.Requests[0].Uri.ToString());
    }

    [Fact]
    public void LinkReportsUnreachableOnNetworkError()
    {
        using var e = new Env(_ => throw new HttpRequestException("refused"));
        var root = JsonDocument.Parse(Assert.IsType<ContentResult>(e.PostLink("{\"url\":\"http://x:1\",\"secret\":\"" + Secret + "\"}")).Content!).RootElement;
        Assert.False(root.GetProperty("reachable").GetBoolean());
        Assert.False(e.Link.Reachable);
        Assert.Equal("unreachable", e.Link.Error);
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

    private static IActionResult PostOverride(Env e, string json)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        e.Controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return e.Controller.LinkOverride().GetAwaiter().GetResult();
    }

    [Fact]
    public void FingerprintIsTheFirst16HexOfSha256()
    {
        // printf 'abc' | sha256sum -> ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad
        Assert.Equal("ba7816bf8f01cfea", MeLink.Fingerprint("abc"));
        Assert.Equal("ba7816bf8f01cfea", new MeLink(new FakeMeSettings { Secret = "abc" }, new FakeHttp(_ => FakeHttp.Text(200, ""))).SecretFingerprint);
        Assert.Null(new MeLink(new FakeMeSettings(), new FakeHttp(_ => FakeHttp.Text(200, ""))).SecretFingerprint);
    }

    [Fact]
    public void InfoExposesFingerprintLinkedUrlAndErrorButNeverTheSecret()
    {
        using var e = new Env(_ => FakeHttp.Text(401, "{\"error\":\"invalid secret\"}"));
        e.Settings.UrlOverride = "http://override:9";
        var before = e.Info();
        Assert.Equal(JsonValueKind.Null, before.GetProperty("meSecretFingerprint").ValueKind);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("meLinkedUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("meError").ValueKind);

        var linkBody = Assert.IsType<ContentResult>(e.PostLink("{\"url\":\"http://pushed:3000/\",\"secret\":\"" + Secret + "\"}")).Content!;
        var info = e.Info();
        Assert.Equal(MeLink.Fingerprint(Secret), info.GetProperty("meSecretFingerprint").GetString());
        Assert.Equal("http://pushed:3000", info.GetProperty("meLinkedUrl").GetString());   // never the override
        Assert.True(info.GetProperty("meLinked").GetBoolean());
        Assert.False(info.GetProperty("meReachable").GetBoolean());
        Assert.Equal("secretMismatch", info.GetProperty("meError").GetString());
        Assert.DoesNotContain(Secret, info.GetRawText());
        Assert.DoesNotContain(Secret, linkBody);
    }

    [Fact]
    public void ANewSecretForgetsTheMismatch()
    {
        var s = new FakeMeSettings { Url = "http://a:1", Secret = Secret };
        var link = new MeLink(s, new FakeHttp(_ => FakeHttp.Text(401, "")));
        link.CheckHealthAsync().GetAwaiter().GetResult();
        Assert.Equal("secretMismatch", link.Error);
        s.Secret = Secret + "new";
        Assert.Null(link.State);
        Assert.Null(link.Error);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(404)]
    [InlineData(500)]
    public void HealthOtherThan2xxOr401IsUnreachable(int status)
    {
        var link = new MeLink(new FakeMeSettings { Url = "http://a:1", Secret = Secret }, new FakeHttp(_ => FakeHttp.Text(status, "")));
        Assert.False(link.CheckHealthAsync().GetAwaiter().GetResult());
        Assert.Equal("unreachable", link.Error);
    }

    [Fact]
    public void LinkOverrideChangesOnlyTheOverrideAndChecksHealth()
    {
        using var e = new Env();
        e.Settings.Url = "http://pushed:3000"; e.Settings.Secret = Secret;
        var r = JsonDocument.Parse(Assert.IsType<ContentResult>(PostOverride(e, "{\"url\":\"http://over:8080/\"}")).Content!).RootElement;
        Assert.True(r.GetProperty("saved").GetBoolean());
        Assert.Equal("http://over:8080", r.GetProperty("effectiveUrl").GetString());
        Assert.True(r.GetProperty("reachable").GetBoolean());
        Assert.Equal("http://over:8080", e.Settings.UrlOverride);
        Assert.Equal("http://pushed:3000", e.Settings.Url);
        Assert.Equal(Secret, e.Settings.Secret);
        Assert.Equal(0, e.Settings.Saves);   // the link (url + secret) is never rewritten
        Assert.Equal("http://over:8080/api/me/health", e.Http.Requests[0].Uri.ToString());
        Assert.DoesNotContain(Secret, r.GetRawText());

        PostOverride(e, "{\"url\":\"\"}");
        Assert.Equal("", e.Settings.UrlOverride);
        Assert.Equal("http://pushed:3000", e.Link.EffectiveUrl);
    }

    [Theory]
    [InlineData("{\"url\":\"ftp://x\"}")]
    [InlineData("{\"url\":\"http://x/?q=1\"}")]
    [InlineData("nope")]
    public void InvalidOverridesAreRejected(string body)
    {
        using var e = new Env();
        e.Settings.UrlOverride = "http://keep:1";
        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(PostOverride(e, body)).StatusCode);
        Assert.Equal("http://keep:1", e.Settings.UrlOverride);
    }

    [Fact]
    public void LinkOverrideIsAdminOnly()
    {
        var m = typeof(SyncController).GetMethod(nameof(SyncController.LinkOverride))!;
        Assert.Equal("LinkOverride", m.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Null(m.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());
    }

    [Fact]
    public void GenericConfigurationSavesKeepTheLinkFields()
    {
        var current = new Jellyfin.Plugin.Jellystics.Configuration.PluginConfiguration { JellysticsUrl = "http://new:1", JellysticsSecret = "new-secret-0123456789", JellysticsUrlOverride = "http://o:2", PbrDbPath = "/old" };
        // A config page that read the configuration before the last Link push posts stale link fields.
        var posted = new Jellyfin.Plugin.Jellystics.Configuration.PluginConfiguration { JellysticsUrl = "http://old:1", JellysticsSecret = "old-secret-0123456789", JellysticsUrlOverride = "", PbrDbPath = "/new" };
        Jellyfin.Plugin.Jellystics.Configuration.PluginConfiguration.KeepLinkFields(posted, current);
        Assert.Equal("http://new:1", posted.JellysticsUrl);
        Assert.Equal("new-secret-0123456789", posted.JellysticsSecret);
        Assert.Equal("http://o:2", posted.JellysticsUrlOverride);
        Assert.Equal("/new", posted.PbrDbPath);
    }

    [Fact]
    public void PluginUpdateConfigurationAppliesKeepLinkFields()
    {
        var m = typeof(Plugin).GetMethod(nameof(Plugin.UpdateConfiguration), new[] { typeof(MediaBrowser.Model.Plugins.BasePluginConfiguration) })!;
        Assert.Equal(typeof(Plugin), m.DeclaringType);
    }
}

public class PluginConfigurationSerializationTests
{
    [Fact]
    public void SecretIsPersistedInXmlButNeverInTheJsonConfigurationApi()
    {
        var config = new Jellyfin.Plugin.Jellystics.Configuration.PluginConfiguration { JellysticsUrl = "http://j:3000", JellysticsSecret = "super-secret-0123456789", JellysticsUrlOverride = "http://o:1" };
        var json = System.Text.Json.JsonSerializer.Serialize(config);
        Assert.DoesNotContain("super-secret-0123456789", json);
        Assert.Contains("http://j:3000", json);

        var xml = new System.Xml.Serialization.XmlSerializer(typeof(Jellyfin.Plugin.Jellystics.Configuration.PluginConfiguration));
        using var writer = new StringWriter();
        xml.Serialize(writer, config);
        Assert.Contains("<JellysticsSecret>super-secret-0123456789</JellysticsSecret>", writer.ToString());
        using var reader = new StringReader(writer.ToString());
        var back = (Jellyfin.Plugin.Jellystics.Configuration.PluginConfiguration)xml.Deserialize(reader)!;
        Assert.Equal("super-secret-0123456789", back.JellysticsSecret);
    }
}
