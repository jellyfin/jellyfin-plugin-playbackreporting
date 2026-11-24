using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jellystics.Me;

/// <summary>Last known state of the link: OK, network/redirect failure, or Jellystics rejecting the secret (401).</summary>
public enum MeLinkState
{
    Ok,
    Unreachable,
    SecretMismatch,
}

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
    private string? _checkedSecret;
    private MeLinkState? _state;
    private Task? _backgroundCheck;

    public MeLink(IMeSettings settings, IHttpClientFactory http)
    {
        _settings = settings; _http = http;
    }

    /// <summary>Gets or sets the timeout of the health check.</summary>
    public TimeSpan HealthTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the URL in use: the admin override when valid, else the pushed URL; empty when none.</summary>
    public string EffectiveUrl => NormalizeUrl(_settings.UrlOverride) ?? NormalizeUrl(_settings.Url) ?? string.Empty;

    /// <summary>Gets the URL last pushed by Jellystics (never the override), or null.</summary>
    public string? LinkedUrl => NormalizeUrl(_settings.Url);

    public string Secret => _settings.Secret ?? string.Empty;

    /// <summary>Gets the first 16 lowercase hex chars of SHA-256(UTF-8 secret), or null when no secret: lets Jellystics detect a stale secret without exposing it.</summary>
    public string? SecretFingerprint => string.IsNullOrEmpty(Secret) ? null : Fingerprint(Secret);

    /// <summary>Gets a value indicating whether an URL and a secret are both known.</summary>
    public bool IsLinked => EffectiveUrl.Length > 0 && !string.IsNullOrWhiteSpace(Secret);

    /// <summary>Gets the last state for the current URL and secret; null when never checked (or either changed since).</summary>
    public MeLinkState? State
    {
        get
        {
            lock (_gate) return IsLinked && _checkedUrl == EffectiveUrl && _checkedSecret == Secret ? _state : null;
        }
    }

    /// <summary>Gets true/false once checked (false for both unreachable and secret mismatch), null when unknown.</summary>
    public bool? Reachable => State is { } s ? s == MeLinkState.Ok : null;

    /// <summary>Gets null, "unreachable" or "secretMismatch".</summary>
    public string? Error => State switch
    {
        MeLinkState.Unreachable => "unreachable",
        MeLinkState.SecretMismatch => "secretMismatch",
        _ => null,
    };

    public static string Fingerprint(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant()[..16];

    /// <summary>Registers the named HttpClient: no automatic redirects (the secret must never follow a Location), no cookies.</summary>
    public static IHttpClientBuilder AddHttpClient(IServiceCollection services) =>
        services.AddHttpClient(HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

    /// <summary>Returns the normalized absolute http(s) URL without trailing slash, or null when the value is not one.</summary>
    public static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>Maps an upstream status to a link state: 2xx OK, 401 secret mismatch, 3xx unreachable; null = says nothing about the link.</summary>
    public static MeLinkState? Classify(HttpStatusCode status) => (int)status switch
    {
        >= 200 and < 300 => MeLinkState.Ok,
        401 => MeLinkState.SecretMismatch,
        >= 300 and < 400 => MeLinkState.Unreachable,
        _ => null,
    };

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

    /// <summary>Validates and persists the admin override ("" clears it). Returns an error message, or null on success.</summary>
    public string? SaveOverride(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _settings.SaveOverride(string.Empty);
            return null;
        }

        var normalized = NormalizeUrl(url);
        if (normalized is null) return "url must be an absolute http or https URL without query or credentials";
        _settings.SaveOverride(normalized);
        return null;
    }

    /// <summary>Records the state of the current effective URL and secret (also fed by the proxies).</summary>
    public void Report(MeLinkState state)
    {
        lock (_gate) { _checkedUrl = EffectiveUrl; _checkedSecret = Secret; _state = state; }
    }

    /// <summary>GET {url}/api/me/health with the secret only; true when Jellystics answers 2xx. 401 = secret mismatch, anything else = unreachable.</summary>
    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!IsLinked) return false;
        var url = EffectiveUrl;
        var secret = Secret;
        MeLinkState state;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(HealthTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url + "/api/me/health");
            request.Headers.TryAddWithoutValidation(SecretHeader, secret);
            using var response = await _http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            state = Classify(response.StatusCode) ?? MeLinkState.Unreachable;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            state = MeLinkState.Unreachable;
        }

        lock (_gate) { _checkedUrl = url; _checkedSecret = secret; _state = state; }
        return state == MeLinkState.Ok;
    }

    /// <summary>Starts a health check in the background when linked and the state is unknown (never blocks).</summary>
    public void EnsureCheckedInBackground()
    {
        if (!IsLinked || State is not null) return;
        lock (_gate)
        {
            if (_backgroundCheck is { IsCompleted: false }) return;
            _backgroundCheck = Task.Run(async () =>
            {
                try { await CheckHealthAsync().ConfigureAwait(false); }
                catch (Exception) { /* reported on the next check */ }
            });
        }
    }
}
