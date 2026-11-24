using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Jellystics.Me;

/// <summary>
/// A response with a status, a content type and selected headers, whose body is either a buffer or an upstream
/// response streamed with a size cap (the connection is aborted past the cap). HEAD gets the headers only.
/// </summary>
public sealed class ProxyResult : IActionResult
{
    public ProxyResult(int statusCode, string? contentType, byte[] body, IReadOnlyDictionary<string, string>? headers = null)
    {
        StatusCode = statusCode; ContentType = contentType; Body = body;
        Headers = headers ?? new Dictionary<string, string>();
    }

    public ProxyResult(int statusCode, string? contentType, HttpResponseMessage upstream, long maxBytes, IReadOnlyDictionary<string, string>? headers = null)
    {
        StatusCode = statusCode; ContentType = contentType; Upstream = upstream; MaxBytes = maxBytes;
        Headers = headers ?? new Dictionary<string, string>();
    }

    public int StatusCode { get; }

    public string? ContentType { get; }

    public byte[]? Body { get; }

    public HttpResponseMessage? Upstream { get; }

    public long MaxBytes { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Gets or sets the time allowed to stream the upstream body.</summary>
    public TimeSpan BodyTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public static ProxyResult Json(int statusCode, string json) =>
        new(statusCode, "application/json; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(json), new Dictionary<string, string>
        {
            ["Cache-Control"] = "private, no-store",
            ["X-Content-Type-Options"] = "nosniff",
        });

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        var response = http.Response;
        try
        {
            response.StatusCode = StatusCode;
            foreach (var (name, value) in Headers) response.Headers[name] = value;
            if (StatusCode is StatusCodes.Status304NotModified or StatusCodes.Status204NoContent) return;
            if (ContentType is not null) response.ContentType = ContentType;
            var head = HttpMethods.IsHead(http.Request.Method);

            if (Body is not null)
            {
                response.ContentLength = Body.Length;
                if (!head) await response.Body.WriteAsync(Body, http.RequestAborted).ConfigureAwait(false);
                return;
            }

            if (Upstream is null) return;
            var length = Upstream.Content.Headers.ContentLength;
            if (length is not null) response.ContentLength = length;
            if (head) return;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            cts.CancelAfter(BodyTimeout);
            await using var source = await Upstream.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxBytes)
                {
                    http.Abort();   // the size cap is checked on Content-Length first; this covers chunked bodies
                    return;
                }

                await response.Body.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
        {
            if (!response.HasStarted && Body is null)
            {
                // Nothing sent yet: the upstream stalled or broke before the first byte.
                response.StatusCode = StatusCodes.Status502BadGateway;
                response.ContentLength = 0;
            }
            else
            {
                http.Abort();
            }
        }
        finally
        {
            Upstream?.Dispose();
        }
    }
}
