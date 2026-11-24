using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellystics.Me;

/// <summary>Injects the « Mes statistiques » client script into jellyfin-web's index.html (pattern of jellyfin-plugin-GetAvatar).</summary>
public sealed class ScriptInjectorMiddleware
{
    public const string ScriptTag = "<script src=\"../Jellystics/Me/client.js\" defer></script>";
    private const string Marker = "Jellystics/Me/client.js";

    private readonly RequestDelegate _next;
    private readonly ILogger<ScriptInjectorMiddleware> _logger;

    public ScriptInjectorMiddleware(RequestDelegate next, ILogger<ScriptInjectorMiddleware> logger)
    {
        _next = next; _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !IsIndexHtmlRequest(context.Request.Path.Value ?? string.Empty))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Read the page uncompressed and never as a 304 with an empty body.
        context.Features.Set<IHttpsCompressionFeature>(null);
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        var status = context.Response.StatusCode;
        var contentType = context.Response.ContentType ?? string.Empty;
        var encoded = context.Response.Headers.ContentEncoding.Count > 0;
        if (status is < 200 or >= 300 || !contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase) || encoded)
        {
            buffer.Seek(0, SeekOrigin.Begin);
            await buffer.CopyToAsync(original).ConfigureAwait(false);
            return;
        }

        var html = Encoding.UTF8.GetString(buffer.ToArray());
        if (!html.Contains(Marker, StringComparison.Ordinal))
        {
            var index = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                html = html.Insert(index, ScriptTag + "\n");
                _logger.LogDebug("Jellystics: « Mes statistiques » script injected into index.html");
            }
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        context.Response.ContentLength = bytes.Length;
        await original.WriteAsync(bytes).ConfigureAwait(false);
    }

    public static bool IsIndexHtmlRequest(string path) =>
        path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/web", StringComparison.OrdinalIgnoreCase);
}
