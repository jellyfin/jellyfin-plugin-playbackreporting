using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Jellystics.Me;

/// <summary>A fully buffered response (status, content type, selected headers, body), easy to assert in tests.</summary>
public sealed class ProxyResult : IActionResult
{
    public ProxyResult(int statusCode, string? contentType, byte[] body, IReadOnlyDictionary<string, string>? headers = null)
    {
        StatusCode = statusCode; ContentType = contentType; Body = body;
        Headers = headers ?? new Dictionary<string, string>();
    }

    public int StatusCode { get; }

    public string? ContentType { get; }

    public byte[] Body { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public string BodyText => System.Text.Encoding.UTF8.GetString(Body);

    public static ProxyResult Json(int statusCode, string json) =>
        new(statusCode, "application/json; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(json), new Dictionary<string, string> { ["Cache-Control"] = "no-store" });

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCode;
        foreach (var (name, value) in Headers) response.Headers[name] = value;
        if (StatusCode is StatusCodes.Status304NotModified or StatusCodes.Status204NoContent) return;
        if (ContentType is not null) response.ContentType = ContentType;
        response.ContentLength = Body.Length;
        await response.Body.WriteAsync(Body, context.HttpContext.RequestAborted).ConfigureAwait(false);
    }
}
