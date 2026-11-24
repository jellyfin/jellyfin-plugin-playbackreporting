namespace Jellyfin.Plugin.Jellystics.Me;

/// <summary>Link to Jellystics for the « Mes statistiques » page: effective URL, secret and last known reachability.</summary>
public sealed class MeLink
{
    public const string HttpClientName = "Jellystics.Me";
    public const string SecretHeader = "X-Jellystics-Secret";
    public const string UserHeader = "X-Jellystics-User";
    public const int MaxSecretLength = 1024;
    public const int MinSecretLength = 16;

    private readonly IMeSettings _settings;
    private readonly IHttpClientFactory _http;
    private readonly object _gate = new();
    private string? _checkedUrl;
    private bool? _reachable;
    private Task? _backgroundCheck;

    public MeLink(IMeSettings settings, IHttpClientFactory http)
    {
        _settings = settings; _http = http;
    }

    /// <summary>Gets or sets the timeout of the health check.</summary>
    public TimeSpan HealthTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the URL in use: the admin override when valid, else the pushed URL; empty when none.</summary>
    public string EffectiveUrl
    {
        get
        {
            var over = NormalizeUrl(_settings.UrlOverride);
            return over ?? NormalizeUrl(_settings.Url) ?? string.Empty;
        }
    }

    public string Secret => _settings.Secret ?? string.Empty;

    /// <summary>Gets a value indicating whether an URL and a secret are both known.</summary>
    public bool IsLinked => EffectiveUrl.Length > 0 && !string.IsNullOrWhiteSpace(Secret);

    /// <summary>Gets the last health result for the current effective URL; null when never checked (or the URL changed since).</summary>
    public bool? Reachable
    {
        get
        {
            lock (_gate) return IsLinked && _checkedUrl == EffectiveUrl ? _reachable : null;
        }
    }

    /// <summary>Returns the normalized absolute http(s) URL without trailing slash, or null when the value is not one.</summary>
    public static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>Validates and persists a link pushed by Jellystics. Returns an error message, or null on success.</summary>
    public string? Save(string? url, string? secret)
    {
        var normalized = NormalizeUrl(url);
        if (normalized is null) return "url must be an absolute http or https URL without query or credentials";
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < MinSecretLength || secret.Length > MaxSecretLength || secret.Any(char.IsWhiteSpace))
            return $"secret must be {MinSecretLength} to {MaxSecretLength} characters without spaces";
        _settings.SaveLink(normalized, secret);
        return null;
    }

    /// <summary>Records the reachability of the current effective URL (also fed by the proxies).</summary>
    public void Report(bool reachable)
    {
        lock (_gate) { _checkedUrl = EffectiveUrl; _reachable = reachable; }
    }

    /// <summary>GET {url}/api/me/health with the secret only; true when Jellystics answers 2xx.</summary>
    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!IsLinked) return false;
        var url = EffectiveUrl;
        bool ok;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(HealthTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url + "/api/me/health");
            request.Headers.TryAddWithoutValidation(SecretHeader, Secret);
            using var response = await _http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            ok = response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            ok = false;
        }

        lock (_gate) { _checkedUrl = url; _reachable = ok; }
        return ok;
    }

    /// <summary>Starts a health check in the background when linked and the reachability is unknown (never blocks).</summary>
    public void EnsureCheckedInBackground()
    {
        if (!IsLinked || Reachable is not null) return;
        lock (_gate)
        {
            if (_backgroundCheck is { IsCompleted: false }) return;
            _backgroundCheck = Task.Run(async () =>
            {
                try { await CheckHealthAsync().ConfigureAwait(false); }
                catch (Exception) { /* reported as unreachable on the next check */ }
            });
        }
    }
}
