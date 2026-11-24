using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.Jellystics.Api;
using Jellyfin.Plugin.Jellystics.Me;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class MeControllerTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";
    private const string UserId = "249B7EE5-788F-4BE5-8208-0F035DA84DFE";
    private const string UserN = "249b7ee5788f4be582080f035da84dfe";

    private static (MeController Controller, FakeHttp Http, FakeMeSettings Settings) Make(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null,
        bool linked = true,
        string path = "/Jellystics/Me/api/overview",
        string query = "",
        string? userClaim = UserId,
        Dictionary<string, string>? headers = null)
    {
        var settings = linked ? new FakeMeSettings { Url = "http://jellystics:3000", Secret = Secret } : new FakeMeSettings();
        var http = new FakeHttp(respond ?? ((_, _) => Task.FromResult(FakeHttp.Text(200, "{\"plays\":3}"))));
        var controller = new MeController(new MeLink(settings, http), http);
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.QueryString = new QueryString(query);
        if (headers is not null) foreach (var (k, v) in headers) ctx.Request.Headers[k] = v;
        var claims = userClaim is null ? new List<Claim>() : new List<Claim> { new(MeController.UserIdClaim, userClaim) };
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Custom"));
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return (controller, http, settings);
    }

    private static ProxyResult Run(Task<IActionResult> task) => Assert.IsType<ProxyResult>(task.GetAwaiter().GetResult());

    private static string Error(ProxyResult r) => JsonDocument.Parse(r.BodyText).RootElement.GetProperty("error").GetString()!;

    [Fact]
    public void RoutesAndAuthorization()
    {
        var t = typeof(MeController);
        Assert.Equal("Jellystics/Me", t.GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.Null(t.GetCustomAttribute<AuthorizeAttribute>());

        var api = t.GetMethod(nameof(MeController.Api))!;
        Assert.Equal("api/{**path}", api.GetCustomAttribute<HttpGetAttribute>()?.Template);
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
    public void ApiRelayAddsSecretAndUserAndStripsClientHeaders()
    {
        var (c, http, _) = Make(query: "?from=2026-01-01&userId=someoneElse&api_key=tok", headers: new()
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
        Assert.Equal(200, r.StatusCode);
        Assert.Equal("{\"plays\":3}", r.BodyText);
        Assert.StartsWith("application/json", r.ContentType);
        Assert.Equal("private, no-store", r.Headers["Cache-Control"]);

        var sent = Assert.Single(http.Requests);
        Assert.Equal("http://jellystics:3000/api/me/overview?from=2026-01-01&userId=someoneElse", sent.Uri.ToString());
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
    public void ApiRelayPassesUpstreamErrorsThrough()
    {
        var (c, _, _) = Make((_, _) => Task.FromResult(FakeHttp.Text(403, "{\"error\":\"unknownUser\"}")));
        var r = Run(c.Api("top"));
        Assert.Equal(403, r.StatusCode);
        Assert.Equal("unknownUser", Error(r));
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
        Assert.Equal(503, r.StatusCode);
        Assert.Equal("notLinked", Error(r));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void ApiAnswers502OnNetworkError()
    {
        var (c, _, _) = Make((_, _) => throw new HttpRequestException("connection refused"));
        var r = Run(c.Api("overview"));
        Assert.Equal(502, r.StatusCode);
        Assert.Equal("unreachable", Error(r));
    }

    [Fact]
    public void ApiAnswers504OnTimeout()
    {
        var (c, _, _) = Make(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return FakeHttp.Text(200, "{}"); });
        c.Timeout = TimeSpan.FromMilliseconds(50);
        var r = Run(c.Api("overview"));
        Assert.Equal(504, r.StatusCode);
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
        Assert.Equal(401, r.StatusCode);
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
    public void TraversalIsRejected(string path)
    {
        var (c, http, _) = Make(path: "/Jellystics/Me/" + path);
        Assert.Equal(400, Run(c.Static(path)).StatusCode);
        Assert.Equal(400, Run(c.Api(path)).StatusCode);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void StaticRootProxiesTheMePage()
    {
        var (c, http, _) = Make((_, _) =>
        {
            var r = FakeHttp.Text(200, "<!doctype html><title>me</title>", "text/html; charset=utf-8");
            r.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc\"");
            return Task.FromResult(r);
        }, path: "/Jellystics/Me/", headers: new() { ["X-Jellystics-Secret"] = "forged", ["Authorization"] = "x" });
        var res = Run(c.Static(null));
        Assert.Equal(200, res.StatusCode);
        Assert.StartsWith("text/html", res.ContentType);
        Assert.Equal("no-cache", res.Headers["Cache-Control"]);
        Assert.Equal("\"abc\"", res.Headers["ETag"]);
        var sent = Assert.Single(http.Requests);
        Assert.Equal("http://jellystics:3000/me/", sent.Uri.ToString());
        Assert.DoesNotContain(sent.Headers.Keys, k => k.StartsWith("X-Jellystics", StringComparison.OrdinalIgnoreCase) || k == "Authorization");
    }

    [Fact]
    public void StaticAssetsKeepContentTypeAndCacheHeaders()
    {
        var (c, http, _) = Make((_, _) =>
        {
            var r = FakeHttp.Text(200, "console.log(1)", "text/javascript");
            r.Headers.TryAddWithoutValidation("Cache-Control", "public, max-age=31536000, immutable");
            return Task.FromResult(r);
        }, path: "/Jellystics/Me/assets/me-abc123.js");
        var res = Run(c.Static("assets/me-abc123.js"));
        Assert.Equal("text/javascript", res.ContentType);
        Assert.Equal("public, max-age=31536000, immutable", res.Headers["Cache-Control"]);
        Assert.Equal("http://jellystics:3000/me/assets/me-abc123.js", http.Requests[0].Uri.ToString());
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
        Assert.Equal(503, r.StatusCode);
        Assert.StartsWith("text/html", r.ContentType);
        Assert.Contains($"<html lang=\"{lang}\">", r.BodyText);
        Assert.Contains(title, r.BodyText);
        Assert.Contains("data-reason=\"notLinked\"", r.BodyText);
        Assert.Contains("href=\"/web/\"", r.BodyText);
        Assert.DoesNotContain("{{", r.BodyText);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void StaticServesTheFallbackWhenUnreachable()
    {
        var (c, _, _) = Make((_, _) => throw new HttpRequestException("refused"), path: "/Jellystics/Me/");
        c.ControllerContext.HttpContext.Request.PathBase = "/jellyfin";
        var r = Run(c.Static(null));
        Assert.Equal(503, r.StatusCode);
        Assert.Contains("data-reason=\"unreachable\"", r.BodyText);
        Assert.Contains("href=\"/jellyfin/web/\"", r.BodyText);
    }

    [Fact]
    public void StaticServesTheFallbackWhenThePageIsMissingUpstream()
    {
        var (c, _, _) = Make((_, _) => Task.FromResult(FakeHttp.Text(404, "404 page not found", "text/plain")), path: "/Jellystics/Me/");
        var r = Run(c.Static(null));
        Assert.Contains("data-reason=\"unreachable\"", r.BodyText);
        var (c2, _, _) = Make((_, _) => Task.FromResult(FakeHttp.Text(404, "nope", "text/plain")), path: "/Jellystics/Me/assets/x.js");
        Assert.Equal(404, Run(c2.Static("assets/x.js")).StatusCode);   // a missing asset stays a 404
    }

    [Fact]
    public void ClientScriptIsServedAsJavascript()
    {
        var (c, _, _) = Make(linked: false);
        var r = Assert.IsType<ProxyResult>(c.ClientScript());
        Assert.Equal(200, r.StatusCode);
        Assert.StartsWith("application/javascript", r.ContentType);
        Assert.Equal("no-cache", r.Headers["Cache-Control"]);
        Assert.Contains("app-user-menu", r.BodyText);
        Assert.Contains("data-jellystics-me", r.BodyText);
    }

    [Fact]
    public void ProxyResultWritesStatusHeadersAndBody()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        var r = new ProxyResult(201, "text/plain", "hi"u8.ToArray(), new Dictionary<string, string> { ["ETag"] = "\"1\"" });
        r.ExecuteResultAsync(new ActionContext { HttpContext = ctx }).GetAwaiter().GetResult();
        Assert.Equal(201, ctx.Response.StatusCode);
        Assert.Equal("text/plain", ctx.Response.ContentType);
        Assert.Equal("\"1\"", ctx.Response.Headers.ETag.ToString());
        Assert.Equal("hi", System.Text.Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray()));
    }
}
