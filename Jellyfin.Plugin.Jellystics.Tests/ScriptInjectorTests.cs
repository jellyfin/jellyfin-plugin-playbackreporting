using System.Text;
using Jellyfin.Plugin.Jellystics.Me;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class ScriptInjectorTests
{
    private const string Page = "<!DOCTYPE html><html><head></head><body><div id=\"reactRoot\"></div></body></html>";

    private static (int Status, string Body, HttpContext Ctx) Run(string path, string body, string contentType = "text/html", int status = 200, Action<HttpRequest>? request = null)
    {
        HttpRequest? seen = null;
        var middleware = new ScriptInjectorMiddleware(async ctx =>
        {
            seen = ctx.Request;
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            ctx.Response.Headers.ETag = "\"etag\"";
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(body));
        }, NullLogger<ScriptInjectorMiddleware>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = path;
        request?.Invoke(ctx.Request);
        var output = new MemoryStream();
        ctx.Response.Body = output;
        middleware.InvokeAsync(ctx).GetAwaiter().GetResult();
        return (ctx.Response.StatusCode, Encoding.UTF8.GetString(output.ToArray()), ctx);
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Theory]
    [InlineData("/web/")]
    [InlineData("/web/index.html")]
    [InlineData("/jellyfin/web/index.html")]
    public void InjectsTheScriptOnceBeforeBody(string path)
    {
        var (status, body, ctx) = Run(path, Page);
        Assert.Equal(200, status);
        Assert.Equal(1, Count(body, ScriptInjectorMiddleware.ScriptTag));
        Assert.Contains(ScriptInjectorMiddleware.ScriptTag + "\n</body>", body);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), ctx.Response.ContentLength);
        Assert.False(ctx.Response.Headers.ContainsKey("ETag"));
        Assert.Equal("<script src=\"../Jellystics/Me/client.js\" defer></script>", ScriptInjectorMiddleware.ScriptTag);
    }

    [Fact]
    public void IsIdempotent()
    {
        var once = Run("/web/index.html", Page).Body;
        var twice = Run("/web/index.html", once).Body;
        Assert.Equal(once, twice);
        Assert.Equal(1, Count(twice, "Jellystics/Me/client.js"));
    }

    [Fact]
    public void StripsConditionalAndCompressionHeaders()
    {
        var (_, _, ctx) = Run("/web/", Page, request: r =>
        {
            r.Headers.IfNoneMatch = "\"x\"";
            r.Headers.IfModifiedSince = "Mon, 01 Jan 2024 00:00:00 GMT";
            r.Headers.AcceptEncoding = "gzip, br";
        });
        Assert.False(ctx.Request.Headers.ContainsKey("If-None-Match"));
        Assert.False(ctx.Request.Headers.ContainsKey("If-Modified-Since"));
        Assert.False(ctx.Request.Headers.ContainsKey("Accept-Encoding"));
    }

    [Fact]
    public void LeavesNonHtmlUntouched()
    {
        var json = "{\"a\":\"</body>\"}";
        Assert.Equal(json, Run("/web/index.html", json, "application/json").Body);
    }

    [Fact]
    public void LeavesOtherPathsUntouched()
    {
        var (_, body, ctx) = Run("/web/main.bundle.js", Page);
        Assert.Equal(Page, body);
        Assert.True(ctx.Response.Headers.ContainsKey("ETag"));
        Assert.Equal(Page, Run("/Items/123", Page).Body);
    }

    [Fact]
    public void LeavesErrorsUntouched()
    {
        var (status, body, _) = Run("/web/index.html", Page, status: 500);
        Assert.Equal(500, status);
        Assert.Equal(Page, body);
    }

    [Fact]
    public void PageWithoutBodyIsUnchanged()
    {
        Assert.Equal("<html>no body</html>", Run("/web/", "<html>no body</html>").Body);
    }
}
