using System.Net;
using System.Security.Claims;
using Jellyfin.Plugin.Jellystics.Me;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Jellystics.Api;

/// <summary>
/// « Mes statistiques »: serves the injected client script, relays /api/me calls to Jellystics for the authenticated
/// user only (the user id comes from Jellyfin's authentication, never from the browser) and proxies the static page.
/// </summary>
[ApiController]
[Route("Jellystics/Me")]
public class MeController : ControllerBase
{
    /// <summary>Jellyfin's user id claim (Jellyfin.Api InternalClaimTypes.UserId, not shipped in the plugin packages).</summary>
    public const string UserIdClaim = "Jellyfin-UserId";

    private static readonly string[] ApiRequestHeaders = { "Accept", "Accept-Language" };
    private static readonly string[] StaticRequestHeaders = { "Accept", "Accept-Language", "If-None-Match", "If-Modified-Since" };
    private static readonly string[] PassedResponseHeaders = { "Cache-Control", "ETag", "Last-Modified", "Expires" };

    private readonly MeLink _link;
    private readonly IHttpClientFactory _http;

    public MeController(MeLink link, IHttpClientFactory http)
    {
        _link = link; _http = http;
    }

    /// <summary>Gets or sets the upstream timeout (15 s).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    [HttpGet("client.js")]
    [AllowAnonymous]
    public IActionResult ClientScript() => new ProxyResult(
        200, "application/javascript; charset=utf-8", MeResources.ClientScript, new Dictionary<string, string> { ["Cache-Control"] = "no-cache" });

    /// <summary>Relays GET {url}/api/me/{path}?{query} with the secret and the authenticated user id.</summary>
    [HttpGet("api/{**path}")]
    [Authorize]
    public async Task<IActionResult> Api([FromRoute] string? path)
    {
        var userId = UserIdFrom(User);
        if (userId is null) return ProxyResult.Json(401, "{\"error\":\"noUser\"}");
        if (!_link.IsLinked) return ProxyResult.Json(503, "{\"error\":\"notLinked\"}");
        path ??= string.Empty;
        if (!IsSafePath(path)) return ProxyResult.Json(400, "{\"error\":\"badPath\"}");

        using var request = new HttpRequestMessage(HttpMethod.Get, _link.EffectiveUrl + "/api/me/" + EscapePath(path) + ForwardedQuery(Request.Query));
        CopyHeaders(request, ApiRequestHeaders);
        request.Headers.TryAddWithoutValidation(MeLink.SecretHeader, _link.Secret);
        request.Headers.TryAddWithoutValidation(MeLink.UserHeader, userId);

        var (result, failure) = await SendAsync(request).ConfigureAwait(false);
        if (result is null) return ProxyResult.Json(failure, "{\"error\":\"unreachable\"}");
        if (!result.Headers.ContainsKey("Cache-Control"))
        {
            // Personal data: never cached by shared caches.
            result = new ProxyResult(result.StatusCode, result.ContentType, result.Body, new Dictionary<string, string>(result.Headers) { ["Cache-Control"] = "private, no-store" });
        }

        return result;
    }

    /// <summary>Proxies the static page from {url}/me/{path} ("" is the page itself); built-in fallback page when not linked or unreachable.</summary>
    [HttpGet("{**path}")]
    [AllowAnonymous]
    public async Task<IActionResult> Static([FromRoute] string? path)
    {
        path ??= string.Empty;
        if (path.Length == 0 && !(Request.Path.Value ?? string.Empty).EndsWith('/'))
        {
            // Relative asset URLs need the trailing slash.
            return Redirect(Request.PathBase + Request.Path + "/" + Request.QueryString);
        }

        if (path.Equals("api", StringComparison.OrdinalIgnoreCase) || path.StartsWith("api/", StringComparison.OrdinalIgnoreCase)) return ProxyResult.Json(404, "{\"error\":\"notFound\"}");
        if (!IsSafePath(path)) return ProxyResult.Json(400, "{\"error\":\"badPath\"}");
        if (!_link.IsLinked) return Fallback(FallbackReason.NotLinked);

        using var request = new HttpRequestMessage(HttpMethod.Get, _link.EffectiveUrl + "/me/" + EscapePath(path));
        CopyHeaders(request, StaticRequestHeaders);
        var (result, _) = await SendAsync(request).ConfigureAwait(false);
        if (result is null) return Fallback(FallbackReason.Unreachable);
        if (result.StatusCode >= 400 && IsPage(path)) return Fallback(FallbackReason.Unreachable);
        return result;
    }

    /// <summary>The authenticated user's id in N format, or null (API keys carry an empty id).</summary>
    public static string? UserIdFrom(ClaimsPrincipal? user)
    {
        var value = user?.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(value, out var id) && id != Guid.Empty ? id.ToString("N") : null;
    }

    /// <summary>Relative path made of plain segments: no "." / "..", no empty inner segment, no backslash, colon or control character.</summary>
    public static bool IsSafePath(string path)
    {
        if (path.Length > 1024 || path.StartsWith('/')) return false;
        if (path.Any(c => c == '\\' || c == ':' || c == '%' || char.IsControl(c))) return false;
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var s = segments[i];
            if (s is "." or "..") return false;
            if (s.Length == 0 && i != segments.Length - 1) return false;
        }

        return true;
    }

    /// <summary>The original query string minus Jellyfin credentials (api_key / ApiKey): the user token never leaves Jellyfin.</summary>
    public static string ForwardedQuery(IQueryCollection query)
    {
        var kept = query.Where(q => !q.Key.Equals("api_key", StringComparison.OrdinalIgnoreCase) && !q.Key.Equals("apikey", StringComparison.OrdinalIgnoreCase))
            .SelectMany(q => q.Value.Select(v => new KeyValuePair<string, string?>(q.Key, v)));
        return QueryString.Create(kept).Value ?? string.Empty;
    }

    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static bool IsPage(string path) => path.Length == 0 || path.EndsWith('/') || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase);

    private void CopyHeaders(HttpRequestMessage request, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (Request.Headers.TryGetValue(name, out var values) && values.Count > 0)
                request.Headers.TryAddWithoutValidation(name, values.ToArray().Where(v => v is not null).Cast<string>());
        }
    }

    /// <summary>Sends and buffers the upstream response; on failure returns the status to answer (502 network error, 504 timeout).</summary>
    private async Task<(ProxyResult? Result, int FailureStatus)> SendAsync(HttpRequestMessage request)
    {
        var aborted = HttpContext?.RequestAborted ?? CancellationToken.None;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        cts.CancelAfter(Timeout);
        try
        {
            using var response = await _http.CreateClient(MeLink.HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            var headers = new Dictionary<string, string>();
            foreach (var name in PassedResponseHeaders)
            {
                if (response.Headers.TryGetValues(name, out var v) || response.Content.Headers.TryGetValues(name, out v)) headers[name] = string.Join(", ", v);
            }

            if (response.IsSuccessStatusCode) _link.Report(true);
            return (new ProxyResult((int)response.StatusCode, response.Content.Headers.ContentType?.ToString(), body, headers), 0);
        }
        catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
        {
            _link.Report(false);
            return (null, (int)HttpStatusCode.GatewayTimeout);
        }
        catch (HttpRequestException)
        {
            _link.Report(false);
            return (null, (int)HttpStatusCode.BadGateway);
        }
    }

    private ProxyResult Fallback(FallbackReason reason)
    {
        var lang = MeResources.PickLanguage(Request.Headers.AcceptLanguage.ToString());
        var html = MeResources.FallbackPage(lang, reason, Request.PathBase + "/web/");
        return new ProxyResult(503, "text/html; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(html), new Dictionary<string, string> { ["Cache-Control"] = "no-store" });
    }
}
