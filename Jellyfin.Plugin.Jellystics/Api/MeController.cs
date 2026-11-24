using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Jellyfin.Plugin.Jellystics.Me;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Jellystics.Api;

/// <summary>
/// « Mes statistiques »: serves the injected client script, relays /api/me calls to Jellystics for the authenticated
/// user only (the user id comes from Jellyfin's authentication, never from the browser) and proxies the static page.
/// Everything that comes from Jellystics runs on the Jellyfin origin, so it is restricted to an allowlist of content
/// types, sent with nosniff, and the page document gets a strict Content-Security-Policy.
/// </summary>
[ApiController]
[Route("Jellystics/Me")]
public class MeController : ControllerBase
{
    /// <summary>Jellyfin's user id claim (Jellyfin.Api InternalClaimTypes.UserId, not shipped in the plugin packages).</summary>
    public const string UserIdClaim = "Jellyfin-UserId";

    /// <summary>Upstream bodies larger than this are refused (Content-Length) or cut (streamed).</summary>
    public const long MaxUpstreamBytes = 10 * 1024 * 1024;

    public const string PageCsp = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'self'; base-uri 'self'; form-action 'self'";
    public const string FallbackCsp = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'";

    private static readonly string[] ApiRequestHeaders = { "Accept", "Accept-Language" };
    private static readonly string[] StaticRequestHeaders = { "Accept", "Accept-Language", "If-None-Match", "If-Modified-Since" };
    private static readonly string[] StaticResponseHeaders = { "Cache-Control", "ETag", "Last-Modified", "Expires" };

    /// <summary>Query keys never forwarded: Jellyfin credentials, and user selectors (Jellystics forces the user anyway; defence in depth).</summary>
    private static readonly HashSet<string> DroppedQueryKeys = new(StringComparer.OrdinalIgnoreCase) { "api_key", "apikey", "userId", "users", "user", "id" };

    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".css", ".json", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".ico", ".woff", ".woff2",
    };

    private static readonly HashSet<string> AssetContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/javascript", "application/javascript", "text/css", "application/json",
        "image/png", "image/jpeg", "image/webp", "image/gif", "image/x-icon", "image/vnd.microsoft.icon",
        "font/woff2", "font/woff",
    };

    private static readonly IReadOnlySet<string> EmptyTypes = new HashSet<string>();
    private static readonly IReadOnlySet<string> HtmlTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "text/html" };
    private static readonly IReadOnlySet<string> JsonTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/json" };
    private static readonly IReadOnlySet<string> SvgTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/svg+xml" };
    private static readonly IReadOnlySet<string> PngTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png" };
    private static readonly IReadOnlySet<string> IcoTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/x-icon", "image/vnd.microsoft.icon" };

    private readonly MeLink _link;
    private readonly IHttpClientFactory _http;

    public MeController(MeLink link, IHttpClientFactory http)
    {
        _link = link; _http = http;
    }

    private enum StaticKind
    {
        Page,
        Asset,
        Refused,
    }

    /// <summary>Gets or sets the upstream timeout until the response headers (15 s).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    [HttpGet("client.js")]
    [HttpHead("client.js")]
    [AllowAnonymous]
    public IActionResult ClientScript() => new ProxyResult(
        200, "application/javascript; charset=utf-8", MeResources.ClientScript, new Dictionary<string, string>
        {
            ["Cache-Control"] = "no-cache",
            ["X-Content-Type-Options"] = "nosniff",
        });

    /// <summary>Relays GET {url}/api/me/{path}?{query} with the secret and the authenticated user id.</summary>
    [HttpGet("api/{**path}")]
    [HttpHead("api/{**path}")]
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

        var (response, failure) = await SendAsync(request).ConfigureAwait(false);
        if (response is null) return ProxyResult.Json(failure, "{\"error\":\"unreachable\"}");
        var status = (int)response.StatusCode;
        if (Refuse(response, status is >= 300 and < 400))
        {
            // Redirects are never followed (the secret must not travel to another host).
            return ProxyResult.Json(502, "{\"error\":\"unreachable\"}");
        }

        if (status == 401)
        {
            // Jellystics rejected the secret: not the user's session (a bare 401 would make the page ask for a new sign-in).
            // Jellystics re-links at its next sync.
            response.Dispose();
            return ProxyResult.Json(502, "{\"error\":\"secretMismatch\"}");
        }

        if (!IsAllowed(response, t => t.Equals("application/json", StringComparison.OrdinalIgnoreCase)) || TooLarge(response))
        {
            response.Dispose();
            return ProxyResult.Json(502, "{\"error\":\"badUpstream\"}");
        }

        // Personal data: whatever Jellystics says, never stored by a cache (the URL is the same for every user).
        return new ProxyResult(status, response.Content.Headers.ContentType?.ToString(), response, MaxUpstreamBytes, new Dictionary<string, string>
        {
            ["Cache-Control"] = "private, no-store",
            ["X-Content-Type-Options"] = "nosniff",
        });
    }

    /// <summary>
    /// Proxies the static page: "" and extension-less routes give the page document ({url}/me/), assets/** with an
    /// allowlisted extension give {url}/me/assets/...; anything else is 404. Built-in fallback page when not linked or unreachable.
    /// </summary>
    [HttpGet("{**path}")]
    [HttpHead("{**path}")]
    [AllowAnonymous]
    public async Task<IActionResult> Static([FromRoute] string? path)
    {
        path ??= string.Empty;
        if (path.Length == 0 && !(Request.Path.Value ?? string.Empty).EndsWith('/'))
        {
            // Relative asset URLs need the trailing slash.
            return Redirect(Request.PathBase + Request.Path + "/" + Request.QueryString);
        }

        if (!IsSafePath(path)) return ProxyResult.Json(400, "{\"error\":\"badPath\"}");
        var kind = Classify(path, out var allowedTypes);
        if (kind == StaticKind.Refused) return ProxyResult.Json(404, "{\"error\":\"notFound\"}");
        var page = kind == StaticKind.Page;
        if (!_link.IsLinked) return page ? Fallback(FallbackReason.NotLinked) : ProxyResult.Json(503, "{\"error\":\"notLinked\"}");

        using var request = new HttpRequestMessage(HttpMethod.Get, _link.EffectiveUrl + "/me/" + (page ? string.Empty : EscapePath(path)));
        CopyHeaders(request, StaticRequestHeaders);
        var (response, _) = await SendAsync(request).ConfigureAwait(false);
        if (response is null) return page ? Fallback(FallbackReason.Unreachable) : ProxyResult.Json(502, "{\"error\":\"unreachable\"}");

        var status = (int)response.StatusCode;
        var headers = new Dictionary<string, string> { ["X-Content-Type-Options"] = "nosniff" };
        foreach (var name in StaticResponseHeaders)
        {
            if (response.Headers.TryGetValues(name, out var v) || response.Content.Headers.TryGetValues(name, out v)) headers[name] = string.Join(", ", v);
        }

        if (page)
        {
            headers["Content-Security-Policy"] = PageCsp;
            headers["Referrer-Policy"] = "same-origin";
        }

        if (status == 304)
        {
            response.Dispose();
            return new ProxyResult(304, null, Array.Empty<byte>(), headers);
        }

        bool Ok(string type) => allowedTypes.Contains(type);
        if (Refuse(response, status is < 200 or >= 300) || !IsAllowed(response, Ok) || TooLarge(response))
        {
            response.Dispose();
            if (page) return Fallback(FallbackReason.Unreachable);
            return status is >= 400 and < 500 ? ProxyResult.Json(status, "{\"error\":\"notFound\"}") : ProxyResult.Json(502, "{\"error\":\"badUpstream\"}");
        }

        return new ProxyResult(status, response.Content.Headers.ContentType?.ToString(), response, MaxUpstreamBytes, headers);
    }

    /// <summary>The authenticated user's id in N format, or null (API keys carry an empty id).</summary>
    public static string? UserIdFrom(ClaimsPrincipal? user)
    {
        var value = user?.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(value, out var id) && id != Guid.Empty ? id.ToString("N") : null;
    }

    /// <summary>Relative path made of plain segments: no "." / "..", no empty inner segment, no backslash, colon, percent or control character.</summary>
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

    /// <summary>The original query string minus Jellyfin credentials and user selectors.</summary>
    public static string ForwardedQuery(IQueryCollection query)
    {
        var kept = query.Where(q => !DroppedQueryKeys.Contains(q.Key))
            .SelectMany(q => q.Value.Select(v => new KeyValuePair<string, string?>(q.Key, v)));
        return QueryString.Create(kept).Value ?? string.Empty;
    }

    /// <summary>
    /// Page: "" and extension-less routes (outside assets/ and locales/). Assets: assets/** (allowlisted extensions),
    /// locales/**/*.json, and root-level *.png / *.svg / *.ico. Everything else (.map, .html, api...) is refused.
    /// <paramref name="allowedTypes"/> is the set of upstream media types accepted for that path.
    /// </summary>
    private static StaticKind Classify(string path, out IReadOnlySet<string> allowedTypes)
    {
        allowedTypes = EmptyTypes;
        if (path.Equals("api", StringComparison.OrdinalIgnoreCase) || path.StartsWith("api/", StringComparison.OrdinalIgnoreCase)) return StaticKind.Refused;
        var inAssets = path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase);
        var inLocales = path.StartsWith("locales/", StringComparison.OrdinalIgnoreCase);
        var last = path.TrimEnd('/');
        last = last[(last.LastIndexOf('/') + 1)..];
        var ext = Path.GetExtension(last);
        if (ext.Length == 0)
        {
            if (inAssets || inLocales) return StaticKind.Refused;
            allowedTypes = HtmlTypes;
            return StaticKind.Page;
        }

        if (path.EndsWith('/')) return StaticKind.Refused;
        var svg = ext.Equals(".svg", StringComparison.OrdinalIgnoreCase);
        if (inAssets)
        {
            if (svg) allowedTypes = SvgTypes;
            else if (AssetExtensions.Contains(ext)) allowedTypes = AssetContentTypes;
        }
        else if (inLocales)
        {
            if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase)) allowedTypes = JsonTypes;
        }
        else if (!path.Contains('/'))
        {
            if (svg) allowedTypes = SvgTypes;
            else if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)) allowedTypes = PngTypes;
            else if (ext.Equals(".ico", StringComparison.OrdinalIgnoreCase)) allowedTypes = IcoTypes;
        }

        return allowedTypes.Count > 0 ? StaticKind.Asset : StaticKind.Refused;
    }

    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static bool IsAllowed(HttpResponseMessage response, Func<string, bool> allowed)
    {
        var type = response.Content.Headers.ContentType?.MediaType;
        return type is not null && allowed(type);
    }

    private static bool TooLarge(HttpResponseMessage response) => response.Content.Headers.ContentLength > MaxUpstreamBytes;

    private bool Refuse(HttpResponseMessage response, bool refuse)
    {
        if (!refuse) return false;
        response.Dispose();
        return true;
    }

    private void CopyHeaders(HttpRequestMessage request, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (Request.Headers.TryGetValue(name, out var values) && values.Count > 0)
                request.Headers.TryAddWithoutValidation(name, values.ToArray().Where(v => v is not null).Cast<string>());
        }
    }

    /// <summary>Sends and returns the upstream response (headers read, body not yet); on failure the status to answer (502 network error, 504 timeout).</summary>
    private async Task<(HttpResponseMessage? Response, int FailureStatus)> SendAsync(HttpRequestMessage request)
    {
        var aborted = HttpContext?.RequestAborted ?? CancellationToken.None;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        cts.CancelAfter(Timeout);
        try
        {
            var response = await _http.CreateClient(MeLink.HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            // Only secret-bearing (api) answers say something about the link; a static 3xx/404 on one file does not.
            if (request.Headers.Contains(MeLink.SecretHeader) && MeLink.Classify(response.StatusCode) is { } state) _link.Report(state);
            return (response, 0);
        }
        catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
        {
            _link.Report(MeLinkState.Unreachable);
            return (null, (int)HttpStatusCode.GatewayTimeout);
        }
        catch (HttpRequestException)
        {
            _link.Report(MeLinkState.Unreachable);
            return (null, (int)HttpStatusCode.BadGateway);
        }
    }

    private ProxyResult Fallback(FallbackReason reason)
    {
        var lang = MeResources.PickLanguage(Request.Headers.AcceptLanguage.ToString());
        var html = MeResources.FallbackPage(lang, reason, Request.PathBase + "/web/");
        return new ProxyResult(503, "text/html; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(html), new Dictionary<string, string>
        {
            ["Cache-Control"] = "no-store",
            ["X-Content-Type-Options"] = "nosniff",
            ["Content-Security-Policy"] = FallbackCsp,
            ["Referrer-Policy"] = "same-origin",
        });
    }
}
