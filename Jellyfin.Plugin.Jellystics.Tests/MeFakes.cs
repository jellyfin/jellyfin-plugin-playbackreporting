using Jellyfin.Plugin.Jellystics.Me;

namespace Jellyfin.Plugin.Jellystics.Tests;

internal sealed class FakeMeSettings : IMeSettings
{
    public string Url { get; set; } = string.Empty;

    public string Secret { get; set; } = string.Empty;

    public string UrlOverride { get; set; } = string.Empty;

    public int Saves { get; private set; }

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
