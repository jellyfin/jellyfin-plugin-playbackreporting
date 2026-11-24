using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Jellyfin.Plugin.Jellystics.Api;
using Jellyfin.Plugin.Jellystics.Me;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Jellystics.Tests;

/// <summary>
/// Real ASP.NET pipeline on Kestrel (127.0.0.1, random port): routing precedence, [Authorize] / RequiresElevation,
/// Kestrel path decoding, StaticFiles + the script injector. Jellyfin's own authentication handler lives in
/// Jellyfin.Api (not in the NuGet packages), so a test scheme emits the same claim (Jellyfin-UserId) and role.
/// </summary>
public sealed class PipelineTests : IAsyncLifetime
{
    private const string Secret = "0123456789abcdef0123456789abcdef";
    private const string UserN = "249b7ee5788f4be582080f035da84dfe";

    private readonly FakeMeSettings _settings = new() { Url = "http://jellystics:3000", Secret = Secret };
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "jellystics-web-" + Guid.NewGuid().ToString("N"));
    private FakeHttp _upstream = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_webRoot, "web"));
        File.WriteAllText(Path.Combine(_webRoot, "web", "index.html"), "<!DOCTYPE html><html><body><div id=\"reactRoot\"></div></body></html>");
        _upstream = new FakeHttp(r => r.RequestUri!.AbsolutePath == "/me/"
            ? FakeHttp.Text(200, "<!doctype html><title>me</title>", "text/html")
            : FakeHttp.Text(200, "{\"path\":\"" + r.RequestUri!.PathAndQuery + "\"}"));

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IMeSettings>(_settings);
        builder.Services.AddSingleton<IHttpClientFactory>(_upstream);
        builder.Services.AddSingleton<MeLink>();
        builder.Services.AddControllers().AddApplicationPart(typeof(MeController).Assembly);
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", null);
        builder.Services.AddAuthorization(o => o.AddPolicy("RequiresElevation", p => p.RequireAuthenticatedUser().RequireRole("Administrator")));

        _app = builder.Build();
        _app.UseMiddleware<ScriptInjectorMiddleware>();
        var files = new PhysicalFileProvider(_webRoot);
        _app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        _app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        _app.UseRouting();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapControllers();
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Directory.Delete(_webRoot, true);
    }

    private async Task<HttpResponseMessage> Get(string url, string? user = null, bool admin = false, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        if (user is not null) request.Headers.Add("X-Test-User", user);
        if (admin) request.Headers.Add("X-Test-Admin", "1");
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task ApiNeedsAJellyfinUserAndForwardsOnlyThatUser()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/Jellystics/Me/api/overview")).StatusCode);
        Assert.Empty(_upstream.Requests);

        var apiKey = await Get("/Jellystics/Me/api/overview", user: Guid.Empty.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, apiKey.StatusCode);
        Assert.Contains("noUser", await apiKey.Content.ReadAsStringAsync());
        Assert.Empty(_upstream.Requests);

        var request = new HttpRequestMessage(HttpMethod.Get, "/Jellystics/Me/api/overview?from=2026-01-01&userId=other");
        request.Headers.Add("X-Test-User", "249B7EE5-788F-4BE5-8208-0F035DA84DFE");
        request.Headers.Add("X-Jellystics-User", "ffffffffffffffffffffffffffffffff");
        var ok = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(ok.Headers.CacheControl!.Private && ok.Headers.CacheControl.NoStore);
        var sent = Assert.Single(_upstream.Requests);
        Assert.Equal("http://jellystics:3000/api/me/overview?from=2026-01-01", sent.Uri.ToString());
        Assert.Equal(UserN, sent.Headers[MeLink.UserHeader]);
    }

    [Fact]
    public async Task ClientScriptAndStaticAreAnonymousAndRoutedBeforeTheCatchAll()
    {
        var js = await Get("/Jellystics/Me/client.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Equal("application/javascript", js.Content.Headers.ContentType?.MediaType);

        var page = await Get("/Jellystics/Me/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("script-src 'self'", string.Join(";", page.Headers.GetValues("Content-Security-Policy")));

        var route = await Get("/Jellystics/Me/history");   // client-side route: same document
        Assert.Equal(HttpStatusCode.OK, route.StatusCode);
        Assert.All(_upstream.Requests, r => Assert.Equal("http://jellystics:3000/me/", r.Uri.ToString()));
    }

    [Fact]
    public async Task MeWithoutTrailingSlashRedirectsServerSide()
    {
        using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = _client.BaseAddress };
        var r = await noRedirect.GetAsync("/Jellystics/Me?x=1");
        Assert.True(r.StatusCode is HttpStatusCode.Found or HttpStatusCode.MovedPermanently);
        Assert.Equal("/Jellystics/Me/?x=1", r.Headers.Location?.OriginalString);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task EncodedSlashCannotReachTheApiThroughTheStaticRoute()
    {
        var r = await Get("/Jellystics/Me/api%2Foverview");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task LinkIsAdminOnly()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsync("/Jellystics/Link", new StringContent("{}"))).StatusCode);
        var asUser = new HttpRequestMessage(HttpMethod.Post, "/Jellystics/LinkOverride") { Content = new StringContent("{\"url\":\"http://evil/\"}") };
        asUser.Headers.Add("X-Test-User", UserN);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(asUser)).StatusCode);
        Assert.Equal("", _settings.UrlOverride);
    }

    [Fact]
    public async Task HeadWorksOnThePage()
    {
        _settings.Secret = string.Empty;   // not linked: fallback page
        var r = await Get("/Jellystics/Me/", method: HttpMethod.Head);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Empty(await r.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task StaticFilesIndexIsInjectedThroughSendFile()
    {
        var conditional = new HttpRequestMessage(HttpMethod.Get, "/web/");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "\"whatever\"");
        conditional.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        foreach (var request in new[] { new HttpRequestMessage(HttpMethod.Get, "/web/"), new HttpRequestMessage(HttpMethod.Get, "/web/index.html"), conditional })
        {
            var r = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var html = await r.Content.ReadAsStringAsync();
            Assert.Equal(1, html.Split("Jellystics/Me/client.js").Length - 1);
            Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(html), r.Content.Headers.ContentLength);
        }
    }

    private sealed class TestAuth : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user)) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(MeController.UserIdClaim, Guid.Parse(user.ToString()).ToString("N")) };
            if (Request.Headers.ContainsKey("X-Test-Admin")) claims.Add(new Claim(ClaimTypes.Role, "Administrator"));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}

/// <summary>The real named HttpClient (MeLink.AddHttpClient) against two local servers: a redirect is never followed.</summary>
public sealed class RedirectTests : IAsyncLifetime
{
    private const string Secret = "0123456789abcdef0123456789abcdef";
    private readonly ConcurrentBag<string> _targetHits = new();
    private WebApplication _app = null!;
    private string _base = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Map("/target/{**rest}", (HttpContext c) =>
        {
            _targetHits.Add(c.Request.Headers[MeLink.SecretHeader].ToString() + "|" + c.Request.Headers[MeLink.UserHeader]);
            return Results.Json(new { ok = true });
        });
        _app.Map("/api/me/{**rest}", (HttpContext c) => Results.Redirect(_base + "/target/" + c.Request.Path));
        _app.Map("/me/{**rest}", (HttpContext c) => Results.Redirect(_base + "/target/page"));
        await _app.StartAsync();
        _base = _app.Urls.First().TrimEnd('/');
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private IHttpClientFactory RealFactory()
    {
        var services = new ServiceCollection();
        MeLink.AddHttpClient(services);
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    [Fact]
    public async Task HealthCheckDoesNotFollowRedirects()
    {
        var link = new MeLink(new FakeMeSettings { Url = _base, Secret = Secret }, RealFactory());
        Assert.False(await link.CheckHealthAsync());
        Assert.Equal("unreachable", link.Error);
        Assert.Empty(_targetHits);
    }

    [Fact]
    public async Task ApiAndPageDoNotFollowRedirects()
    {
        var factory = RealFactory();
        var link = new MeLink(new FakeMeSettings { Url = _base, Secret = Secret }, factory);
        var controller = new MeController(link, factory);
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/Jellystics/Me/";
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(MeController.UserIdClaim, Guid.NewGuid().ToString()) }, "t"));
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = ctx };

        var api = ResultRunner.Exec(await controller.Api("overview"));
        Assert.Equal(502, api.Status);
        var page = ResultRunner.Exec(await controller.Static(null));
        Assert.Contains("data-reason=\"unreachable\"", page.Body);
        Assert.Empty(_targetHits);   // neither the secret nor the user id reached the redirect target
    }
}
