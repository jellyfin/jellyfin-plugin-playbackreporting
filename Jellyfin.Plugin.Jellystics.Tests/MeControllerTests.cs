using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.Jellystics.Api;
using Jellyfin.Plugin.Jellystics.Me;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using static Jellyfin.Plugin.Jellystics.Tests.ResultRunner;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class MeControllerTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";
    private const string UserId = "249B7EE5-788F-4BE5-8208-0F035DA84DFE";
    private const string UserN = "249b7ee5788f4be582080f035da84dfe";

    private static (MeController Controller, FakeHttp Http, MeLink Link) Make(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null,
        bool linked = true,
        string path = "/Jellystics/Me/api/overview",
        string query = "",
        string? userClaim = UserId,
        Dictionary<string, string>? headers = null)
    {
        var settings = linked ? new FakeMeSettings { Url = "http://jellystics:3000", Secret = Secret } : new FakeMeSettings();
        var http = new FakeHttp(respond ?? ((_, _) => Task.FromResult(FakeHttp.Text(200, "{\"plays\":3}"))));
        var link = new MeLink(settings, http);
        var controller = new MeController(link, http);
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.QueryString = new QueryString(query);
        if (headers is not null) foreach (var (k, v) in headers) ctx.Request.Headers[k] = v;
        var claims = userClaim is null ? new List<Claim>() : new List<Claim> { new(MeController.UserIdClaim, userClaim) };
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Custom"));
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return (controller, http, link);
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond(int status, string body, string contentType, Action<HttpResponseMessage>? tweak = null) =>
        (_, _) =>
        {
            var r = FakeHttp.Text(status, body, contentType);
            tweak?.Invoke(r);
            return Task.FromResult(r);
        };

    private static Executed Run(Task<IActionResult> task, string method = "GET") => Exec(task.GetAwaiter().GetResult(), method);

    private static string Error(Executed r) => JsonDocument.Parse(r.Body).RootElement.GetProperty("error").GetString()!;

    [Fact]
    public void RoutesAndAuthorization()
    {
        var t = typeof(MeController);
        Assert.Equal("Jellystics/Me", t.GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.Null(t.GetCustomAttribute<AuthorizeAttribute>());

        var api = t.GetMethod(nameof(MeController.Api))!;
        Assert.Equal("api/{**path}", api.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.Equal("api/{**path}", api.GetCustomAttribute<HttpHeadAttribute>()?.Template);
        var auth = api.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(auth);
        Assert.Null(auth!.Policy);   // any authenticated user, not only admins
        Assert.Null(api.GetCustomAttribute<AllowAnonymousAttribute>());

        Assert.NotNull(t.GetMethod(nameof(MeController.Static))!.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal("{**path}", t.GetMethod(nameof(MeController.Static))!.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.NotNull(t.GetMethod(nameof(MeController.ClientScript))!.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal("client.js", t.GetMethod(nameof(MeController.ClientScript))!.GetCustomAttribute<HttpGetAttribute>()?.Template);
    }

    [Fact]
    public void ApiRelayAddsSecretAndUserAndStripsClientHeadersAndUserParams()
    {
        var (c, http, _) = Make(query: "?from=2026-01-01&userId=someoneElse&USERS=a,b&user=x&Id=y&api_key=tok&ApiKey=tok&limit=5", headers: new()
        {
            ["X-Jellystics-User"] = "ffffffffffffffffffffffffffffffff",
            ["X-Jellystics-Secret"] = "forged",
            ["X-Jellystics-Other"] = "x",
            ["Authorization"] = "MediaBrowser Token=\"usertoken\"",
            ["X-Emby-Token"] = "usertoken",
            ["Cookie"] = "a=b",
            ["Accept-Language"] = "fr-FR",
        });
        var r = Run(c.Api("overview"));
        Assert.Equal(200, r.Status);
        Assert.Equal("{\"plays\":3}", r.Body);
        Assert.StartsWith("application/json", r.Headers["Content-Type"]);
        Assert.Equal("nosniff", r.Headers["X-Content-Type-Options"]);

        var sent = Assert.Single(http.Requests);
        Assert.Equal("http://jellystics:3000/api/me/overview?from=2026-01-01&limit=5", sent.Uri.ToString());
        Assert.Equal(Secret, sent.Headers["X-Jellystics-Secret"]);
        Assert.Equal(UserN, sent.Headers["X-Jellystics-User"]);
        Assert.False(sent.Headers.ContainsKey("X-Jellystics-Other"));
        Assert.False(sent.Headers.ContainsKey("Authorization"));
        Assert.False(sent.Headers.ContainsKey("X-Emby-Token"));
        Assert.False(sent.Headers.ContainsKey("Cookie"));
        Assert.Equal("fr-FR", sent.Headers["Accept-Language"]);
        Assert.Equal(2, sent.Headers.Keys.Count(k => k.StartsWith("X-Jellystics-", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ApiCacheControlIsAlwaysPrivateNoStore()
    {
        var (c, _, _) = Make(Respond(200, "{}", "application/json", r =>
        {
            r.Headers.TryAddWithoutValidation("Cache-Control", "public, max-age=3600");
            r.Headers.ETag = new EntityTagHeaderValue("\"e\"");
            r.Content.Headers.LastModified = DateTimeOffset.UtcNow;
        }));
        var res = Run(c.Api("overview"));
        Assert.Equal("private, no-store", res.Headers["Cache-Control"]);
        Assert.False(res.Headers.ContainsKey("ETag"));
        Assert.False(res.Headers.ContainsKey("Last-Modified"));
    }

    [Fact]
    public void ApiRelayPassesUpstreamJsonErrorsThrough()
    {
        var (c, _, _) = Make(Respond(403, "{\"error\":\"unknownUser\"}", "application/json"));
        var r = Run(c.Api("top"));
        Assert.Equal(403, r.Status);
        Assert.Equal("unknownUser", Error(r));
    }

    [Fact]
    public void ApiUpstream401IsASecretMismatchNotASessionError()
    {
        var (c, _, link) = Make(Respond(401, "{\"error\":\"invalid secret\"}", "application/json"));
        var r = Run(c.Api("overview"));
        Assert.Equal(502, r.Status);   // not 401: the me app would ask for a new sign-in
        Assert.Equal("secretMismatch", Error(r));
        Assert.Equal(MeLinkState.SecretMismatch, link.State);
        Assert.Equal("secretMismatch", link.Error);
        Assert.False(link.Reachable);
    }

    [Fact]
    public void ApiSuccessMarksTheLinkOk()
    {
        var (c, _, link) = Make();
        Run(c.Api("overview"));
        Assert.Equal(MeLinkState.Ok, link.State);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    public void ApiRedirectIsTreatedAsUnreachable(int status)
    {
        var (c, http, link) = Make(Respond(status, "", "text/plain", r => r.Headers.Location = new Uri("http://elsewhere/")));
        var r = Run(c.Api("overview"));
        Assert.Equal(502, r.Status);
        Assert.Equal("unreachable", Error(r));
        Assert.Single(http.Requests);
        Assert.Equal("unreachable", link.Error);
    }

    [Fact]
    public void ApiRefusesNonJsonAndOversizedBodies()
    {
        var (c, _, _) = Make(Respond(200, "<script>alert(1)</script>", "text/html"));
        Assert.Equal(502, Run(c.Api("overview")).Status);
        var (c2, _, _) = Make(Respond(200, "{}", "application/json", r => r.Content.Headers.ContentLength = MeController.MaxUpstreamBytes + 1));
        Assert.Equal(502, Run(c2.Api("overview")).Status);
    }

    [Fact]
    public void ApiRelayKeepsNestedPathsAndEscapesSegments()
    {
        var (c, http, _) = Make();
        Run(c.Api("history/page 2"));
        Assert.Equal("http://jellystics:3000/api/me/history/page%202", http.Requests[0].Uri.AbsoluteUri);
    }

    [Fact]
    public void ApiAnswers503WhenNotLinked()
    {
        var (c, http, _) = Make(linked: false);
        var r = Run(c.Api("health"));
        Assert.Equal(503, r.Status);
        Assert.Equal("notLinked", Error(r));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void ApiAnswers502OnNetworkError()
    {
        var (c, _, link) = Make((_, _) => throw new HttpRequestException("connection refused"));
        var r = Run(c.Api("overview"));
        Assert.Equal(502, r.Status);
        Assert.Equal("unreachable", Error(r));
        Assert.Equal("unreachable", link.Error);
    }

    [Fact]
    public void ApiAnswers504OnTimeout()
    {
        var (c, _, _) = Make(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return FakeHttp.Text(200, "{}"); });
        c.Timeout = TimeSpan.FromMilliseconds(50);
        var r = Run(c.Api("overview"));
        Assert.Equal(504, r.Status);
        Assert.Equal("unreachable", Error(r));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]   // API key: no user
    [InlineData("not-a-guid")]
    public void ApiRefusesRequestsWithoutAUserId(string? claim)
    {
        var (c, http, _) = Make(userClaim: claim);
        var r = Run(c.Api("overview"));
        Assert.Equal(401, r.Status);
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("../health")]
    [InlineData("a/../../etc/passwd")]
    [InlineData("./x")]
    [InlineData("/etc/passwd")]
    [InlineData("a\\..\\b")]
    [InlineData("C:/windows")]
    [InlineData("http://evil/")]
    [InlineData("a//b")]
    [InlineData("%2e%2e/x")]
    [InlineData("assets/../../x.js")]
    public void TraversalIsRejected(string path)
    {
        var (c, http, _) = Make(path: "/Jellystics/Me/" + path);
        Assert.Equal(400, Run(c.Static(path)).Status);
        Assert.Equal(400, Run(c.Api(path)).Status);
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("history")]
    [InlineData("history/2026/")]
    public void PageRoutesProxyTheMeDocumentWithCspAndNosniff(string path)
    {
        var (c, http, _) = Make(Respond(200, "<!doctype html><title>me</title>", "text/html; charset=utf-8", r =>
        {
            r.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            r.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
        }), path: "/Jellystics/Me/" + path, headers: new() { ["X-Jellystics-Secret"] = "forged", ["Authorization"] = "x" });
        var res = Run(c.Static(path));
        Assert.Equal(200, res.Status);
        Assert.Equal("<!doctype html><title>me</title>", res.Body);
        Assert.StartsWith("text/html", res.Headers["Content-Type"]);
        Assert.Equal("no-cache", res.Headers["Cache-Control"]);
        Assert.Equal("\"abc\"", res.Headers["ETag"]);
        Assert.Equal("nosniff", res.Headers["X-Content-Type-Options"]);
        Assert.Equal(MeController.PageCsp, res.Headers["Content-Security-Policy"]);
        Assert.Contains("script-src 'self'", res.Headers["Content-Security-Policy"]);
        Assert.Equal("same-origin", res.Headers["Referrer-Policy"]);
        var sent = Assert.Single(http.Requests);
        Assert.Equal("http://jellystics:3000/me/", sent.Uri.ToString());
        Assert.DoesNotContain(sent.Headers.Keys, k => k.StartsWith("X-Jellystics", StringComparison.OrdinalIgnoreCase) || k == "Authorization");
    }

    [Theory]
    [InlineData("assets/me-abc123.js", "text/javascript; charset=utf-8")]
    [InlineData("assets/me-abc123.js", "application/javascript")]
    [InlineData("assets/me-abc123.css", "text/css")]
    [InlineData("assets/logo.png", "image/png")]
    [InlineData("assets/f.woff2", "font/woff2")]
    [InlineData("assets/icons/x.ico", "image/vnd.microsoft.icon")]
    public void AssetsKeepContentTypeAndCacheHeaders(string path, string contentType)
    {
        var (c, http, _) = Make(Respond(200, "body", contentType, r => r.Headers.TryAddWithoutValidation("Cache-Control", "public, max-age=31536000, immutable")),
            path: "/Jellystics/Me/" + path);
        var res = Run(c.Static(path));
        Assert.Equal(200, res.Status);
        Assert.Equal("body", res.Body);
        Assert.Equal(contentType, res.Headers["Content-Type"]);
        Assert.Equal("public, max-age=31536000, immutable", res.Headers["Cache-Control"]);
        Assert.Equal("nosniff", res.Headers["X-Content-Type-Options"]);
        Assert.False(res.Headers.ContainsKey("Content-Security-Policy"));
        Assert.Equal("http://jellystics:3000/me/" + path, http.Requests[0].Uri.ToString());
    }

    [Theory]
    [InlineData("assets/me-abc123.js.map")]
    [InlineData("me.js.map")]
    [InlineData("assets/index.html")]
    [InlineData("assets/x.exe")]
    [InlineData("logo.gif")]
    [InlineData("sub/logo-64.png")]
    [InlineData("locales/fr/translation.txt")]
    [InlineData("locales/fr")]
    [InlineData("locales/fr/translation.json.map")]
    [InlineData("logo.svg.map")]
    [InlineData("index.html")]
    [InlineData("assets/")]
    [InlineData("assets/noext")]
    [InlineData("api")]
    [InlineData("api/overview")]
    public void OtherStaticPathsAreRefusedWithoutCallingUpstream(string path)
    {
        var (c, http, _) = Make(path: "/Jellystics/Me/" + path);
        Assert.Equal(404, Run(c.Static(path)).Status);
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("locales/fr/translation.json", "application/json; charset=utf-8")]
    [InlineData("locales/es-ES/me.json", "application/json")]
    [InlineData("logo-64.png", "image/png")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("favicon.ico", "image/x-icon")]
    [InlineData("assets/icon.svg", "image/svg+xml")]
    public void LocalesAndRootImagesAreServed(string path, string contentType)
    {
        var (c, http, _) = Make(Respond(200, "x", contentType), path: "/Jellystics/Me/" + path);
        var res = Run(c.Static(path));
        Assert.Equal(200, res.Status);
        Assert.Equal(contentType, res.Headers["Content-Type"]);
        Assert.Equal("nosniff", res.Headers["X-Content-Type-Options"]);
        Assert.Equal("http://jellystics:3000/me/" + path, http.Requests[0].Uri.ToString());
    }

    [Theory]
    [InlineData("logo.svg", "text/html")]
    [InlineData("logo.svg", "image/png")]
    [InlineData("logo-64.png", "image/svg+xml")]
    [InlineData("locales/fr/translation.json", "text/html")]
    [InlineData("locales/fr/translation.json", "text/javascript")]
    [InlineData("assets/me.js", "image/svg+xml")]
    public void ContentTypeMustMatchThePathKind(string path, string contentType)
    {
        var (c, _, _) = Make(Respond(200, "<svg onload=alert(1)>", contentType), path: "/Jellystics/Me/" + path);
        var res = Run(c.Static(path));
        Assert.Equal(502, res.Status);
        Assert.DoesNotContain("<svg", res.Body);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/octet-stream")]
    [InlineData(null)]
    public void AssetsWithADisallowedContentTypeAreRefused(string? contentType)
    {
        var (c, _, _) = Make((_, _) =>
        {
            var r = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent("<script>x</script>"u8.ToArray()) };
            if (contentType is not null) r.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            return Task.FromResult(r);
        }, path: "/Jellystics/Me/assets/a.js");
        var res = Run(c.Static("assets/a.js"));
        Assert.Equal(502, res.Status);
        Assert.DoesNotContain("<script>", res.Body);
    }

    [Fact]
    public void PageWithANonHtmlContentTypeGivesTheFallback()
    {
        var (c, _, _) = Make(Respond(200, "{}", "application/json"), path: "/Jellystics/Me/");
        var r = Run(c.Static(null));
        Assert.Equal(503, r.Status);
        Assert.Contains("data-reason=\"unreachable\"", r.Body);
    }

    [Fact]
    public void OversizedUpstreamBodiesAreRefusedOrCut()
    {
        var (c, _, _) = Make(Respond(200, "x", "text/javascript", r => r.Content.Headers.ContentLength = MeController.MaxUpstreamBytes + 1), path: "/Jellystics/Me/assets/a.js");
        Assert.Equal(502, Run(c.Static("assets/a.js")).Status);

        // Chunked body without Content-Length: streamed, the connection is aborted past the cap.
        using var upstream = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[300])) };
        var result = new ProxyResult(200, "text/javascript", upstream, 100);
        Assert.True(Exec(result).Aborted);
    }

    [Fact]
    public void StaticRedirectFromUpstreamIsNotFollowed()
    {
        var (c, http, link) = Make(Respond(302, "", "text/plain", r => r.Headers.Location = new Uri("http://sso.example/login")), path: "/Jellystics/Me/");
        var r = Run(c.Static(null));
        Assert.Contains("data-reason=\"unreachable\"", r.Body);
        Assert.Single(http.Requests);
        Assert.Null(link.Error);   // a static redirect does not change the link state (no secret involved)
    }

    [Fact]
    public void StaticNotModifiedIsPassedThrough()
    {
        var (c, http, _) = Make(Respond(304, "", "text/html", r => r.Headers.ETag = new EntityTagHeaderValue("\"abc\"")),
            path: "/Jellystics/Me/", headers: new() { ["If-None-Match"] = "\"abc\"" });
        var r = Run(c.Static(null));
        Assert.Equal(304, r.Status);
        Assert.Equal("", r.Body);
        Assert.Equal("\"abc\"", http.Requests[0].Headers["If-None-Match"]);
    }

    [Fact]
    public void StaticWithoutTrailingSlashRedirects()
    {
        var (c, http, _) = Make(path: "/Jellystics/Me", query: "?x=1");
        var r = Assert.IsType<RedirectResult>(c.Static(null).GetAwaiter().GetResult());
        Assert.Equal("/Jellystics/Me/?x=1", r.Url);
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("fr-FR,fr;q=0.9", "fr", "Statistiques indisponibles")]
    [InlineData("es-ES", "es", "Estadísticas no disponibles")]
    [InlineData("de-DE,en;q=0.5", "en", "Statistics unavailable")]
    [InlineData("", "en", "Statistics unavailable")]
    public void StaticServesTheLocalizedFallbackWhenNotLinked(string acceptLanguage, string lang, string title)
    {
        var (c, http, _) = Make(linked: false, path: "/Jellystics/Me/", headers: new() { ["Accept-Language"] = acceptLanguage });
        var r = Run(c.Static(null));
        Assert.Equal(503, r.Status);
        Assert.StartsWith("text/html", r.Headers["Content-Type"]);
        Assert.Contains($"<html lang=\"{lang}\">", r.Body);
        Assert.Contains(title, r.Body);
        Assert.Contains("data-reason=\"notLinked\"", r.Body);
        Assert.Contains("href=\"/web/\"", r.Body);
        Assert.DoesNotContain("{{", r.Body);
        Assert.Equal("nosniff", r.Headers["X-Content-Type-Options"]);
        Assert.Equal(MeController.FallbackCsp, r.Headers["Content-Security-Policy"]);
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("history")]
    public void PageRoutesServeTheFallbackWhenUnreachable(string path)
    {
        var (c, _, _) = Make((_, _) => throw new HttpRequestException("refused"), path: "/Jellystics/Me/" + path);
        c.ControllerContext.HttpContext.Request.PathBase = "/jellyfin";
        var r = Run(c.Static(path));
        Assert.Equal(503, r.Status);
        Assert.Contains("data-reason=\"unreachable\"", r.Body);
        Assert.Contains("href=\"/jellyfin/web/\"", r.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("history")]
    public void PageRoutesServeTheFallbackWhenUpstreamFails(string path)
    {
        var (c, _, _) = Make(Respond(404, "404 page not found", "text/plain"), path: "/Jellystics/Me/" + path);
        var r = Run(c.Static(path));
        Assert.Contains("data-reason=\"unreachable\"", r.Body);
        Assert.DoesNotContain("404 page not found", r.Body);
        var (c2, _, _) = Make(Respond(500, "boom", "text/plain"), path: "/Jellystics/Me/" + path);
        Assert.Contains("data-reason=\"unreachable\"", Run(c2.Static(path)).Body);
    }

    [Fact]
    public void MissingAssetsStayA404WithoutTheUpstreamBody()
    {
        var (c, _, _) = Make(Respond(404, "upstream text", "text/plain"), path: "/Jellystics/Me/assets/x.js");
        var r = Run(c.Static("assets/x.js"));
        Assert.Equal(404, r.Status);
        Assert.DoesNotContain("upstream text", r.Body);
    }

    [Fact]
    public void HeadReturnsHeadersOnly()
    {
        var (c, _, _) = Make(Respond(200, "<!doctype html>", "text/html"), path: "/Jellystics/Me/");
        var r = Run(c.Static(null), "HEAD");
        Assert.Equal(200, r.Status);
        Assert.Equal("", r.Body);
        Assert.Equal(MeController.PageCsp, r.Headers["Content-Security-Policy"]);
        var js = Exec(c.ClientScript(), "HEAD");
        Assert.Equal(200, js.Status);
        Assert.Equal("", js.Body);
    }

    [Fact]
    public void ClientScriptIsServedAsJavascript()
    {
        var (c, _, _) = Make(linked: false);
        var r = Exec(c.ClientScript());
        Assert.Equal(200, r.Status);
        Assert.StartsWith("application/javascript", r.Headers["Content-Type"]);
        Assert.Equal("no-cache", r.Headers["Cache-Control"]);
        Assert.Equal("nosniff", r.Headers["X-Content-Type-Options"]);
        Assert.Contains("app-user-menu", r.Body);
        Assert.Contains("data-jellystics-me", r.Body);
    }

    [Fact]
    public void SecretNeverAppearsInResponses()
    {
        var (c, _, _) = Make(Respond(401, "{\"error\":\"invalid secret\"}", "application/json"), path: "/Jellystics/Me/");
        Assert.DoesNotContain(Secret, Run(c.Api("overview")).Body);
        var (c2, _, _) = Make((_, _) => throw new HttpRequestException("x"), path: "/Jellystics/Me/");
        var fallback = Run(c2.Static(null));
        Assert.DoesNotContain(Secret, fallback.Body);
        Assert.DoesNotContain(fallback.Headers.Values, v => v.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, Exec(c2.ClientScript()).Body);
    }

    [Fact]
    public void ProxyResultWritesStatusHeadersAndBody()
    {
        var r = Exec(new ProxyResult(201, "text/plain", "hi"u8.ToArray(), new Dictionary<string, string> { ["ETag"] = "\"1\"" }));
        Assert.Equal(201, r.Status);
        Assert.Equal("text/plain", r.Headers["Content-Type"]);
        Assert.Equal("\"1\"", r.Headers["ETag"]);
        Assert.Equal("hi", r.Body);
    }
}
