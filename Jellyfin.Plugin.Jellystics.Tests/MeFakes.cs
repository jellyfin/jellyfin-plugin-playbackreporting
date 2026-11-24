using Jellyfin.Plugin.Jellystics.Me;

namespace Jellyfin.Plugin.Jellystics.Tests;

internal sealed class FakeMeSettings : IMeSettings
{
    public string Url { get; set; } = string.Empty;

    public string Secret { get; set; } = string.Empty;

    public string UrlOverride { get; set; } = string.Empty;

    public int Saves { get; private set; }

    public void SaveOverride(string url) => UrlOverride = url;

    public void SaveLink(string url, string secret)
    {
        Url = url; Secret = secret; Saves++;
    }
}

/// <summary>IHttpClientFactory over a fake handler that records every request (headers copied before disposal).</summary>
internal sealed class FakeHttp : IHttpClientFactory
{
    private readonly Handler _handler;

    public FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _handler = new Handler(this, respond);
    }

    public FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((r, _) => Task.FromResult(respond(r)))
    {
    }

    public List<Recorded> Requests { get; } = new();

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);

    public static HttpResponseMessage Text(int status, string body, string contentType = "application/json")
    {
        var r = new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(body) };
        r.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        return r;
    }

    internal sealed record Recorded(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers);

    private sealed class Handler : HttpMessageHandler
    {
        private readonly FakeHttp _owner;
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public Handler(FakeHttp owner, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _owner = owner; _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            lock (_owner.Requests) _owner.Requests.Add(new Recorded(request.Method, request.RequestUri!, headers));
            return _respond(request, cancellationToken);
        }
    }
}

internal static class ResultRunner
{
    internal sealed record Executed(int Status, Dictionary<string, string> Headers, string Body, bool Aborted);

    /// <summary>Executes an action result against a fresh HttpContext and returns what the client would get.</summary>
    public static Executed Exec(Microsoft.AspNetCore.Mvc.IActionResult result, string method = "GET")
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Method = method;
        var lifetime = new Lifetime();
        ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature>(lifetime);
        var body = new MemoryStream();
        ctx.Response.Body = body;
        result.ExecuteResultAsync(new Microsoft.AspNetCore.Mvc.ActionContext { HttpContext = ctx }).GetAwaiter().GetResult();
        var headers = ctx.Response.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        if (ctx.Response.ContentType is not null) headers["Content-Type"] = ctx.Response.ContentType;
        return new Executed(ctx.Response.StatusCode, headers, System.Text.Encoding.UTF8.GetString(body.ToArray()), lifetime.Aborted);
    }

    private sealed class Lifetime : Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; } = CancellationToken.None;

        public bool Aborted { get; private set; }

        public void Abort() => Aborted = true;
    }
}
