using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Jellystics.Me;
using Jellyfin.Plugin.Jellystics.Models;
using Jellyfin.Plugin.Jellystics.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Jellystics.Api;

/// <summary>Cursor based sync API read by Jellystics. JSON is camelCase, whatever Jellyfin's own serializer defaults to.</summary>
[ApiController]
[Route("Jellystics")]
[Authorize(Policy = "RequiresElevation")]
public class SyncController : ControllerBase
{
    public const int SchemaVersion = 1;
    public const int DefaultLimit = 500;
    public const int MaxLimit = 1000;
    public const int MaxImportItems = 500;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new UtcDateTimeOffsetConverter(), new NullableUtcDateTimeOffsetConverter() },
    };

    private readonly PlayStore _plays;
    private readonly AnnexStore _annex;
    private readonly WriteQueue _queue;
    private readonly MeLink _link;

    public SyncController(PlayStore plays, AnnexStore annex, WriteQueue queue, MeLink link)
    {
        _plays = plays; _annex = annex; _queue = queue; _link = link;
    }

    [HttpGet("Info")]
    public IActionResult Info()
    {
        _link.EnsureCheckedInBackground();
        return Camel(new
        {
            pluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            schemaVersion = SchemaVersion,
            cursor = _plays.Cursor(),   // global max Version (one counter shared by every table)
            databaseId = _annex.GetMeta(JellysticsDb.DatabaseIdKey),   // changes when the database is recreated (reinstall, lost db)
            firstPlayAt = _plays.FirstPlayAt(),
            counts = new { plays = _plays.Count(), libraryEvents = _annex.CountLibrary(), userDataEvents = _annex.CountUserData(), logins = _annex.CountLogins() },
            serverTimeZone = TimeZoneInfo.Local.Id,
            meLinked = _link.IsLinked,   // url and secret known (pushed by POST /Jellystics/Link)
            meReachable = _link.Reachable,   // last health / relay result, null = not checked yet; false for unreachable and secretMismatch
            meError = _link.Error,   // null, "unreachable" or "secretMismatch" (Jellystics answered 401: re-push the link)
            meLinkedUrl = _link.LinkedUrl,   // url last pushed by Jellystics (never the override)
            meSecretFingerprint = _link.SecretFingerprint,   // first 16 hex of SHA-256(secret); the secret itself is never returned
        });
    }

    /// <summary>Jellystics pushes its URL (as seen from Jellyfin) and the /api/me secret; the plugin stores them and checks the health endpoint.</summary>
    [HttpPost("Link")]
    public async Task<IActionResult> Link()
    {
        LinkRequest? request;
        try { request = await JsonSerializer.DeserializeAsync<LinkRequest>(Request.Body, JsonOptions).ConfigureAwait(false); }
        catch (JsonException) { return BadRequest(new { error = "Invalid JSON" }); }
        if (request is null) return BadRequest(new { error = "Body must be {\"url\": \"...\", \"secret\": \"...\"}" });
        var error = _link.Save(request.Url, request.Secret);
        if (error is not null) return BadRequest(new { error });
        var reachable = await _link.CheckHealthAsync(HttpContext?.RequestAborted ?? CancellationToken.None).ConfigureAwait(false);
        return Camel(new { linked = true, reachable, error = _link.Error });
    }

    /// <summary>Admin override of the Jellystics URL ("" clears it). Changes only that field, then checks the health endpoint.</summary>
    [HttpPost("LinkOverride")]
    public async Task<IActionResult> LinkOverride()
    {
        LinkOverrideRequest? request;
        try { request = await JsonSerializer.DeserializeAsync<LinkOverrideRequest>(Request.Body, JsonOptions).ConfigureAwait(false); }
        catch (JsonException) { return BadRequest(new { error = "Invalid JSON" }); }
        if (request is null) return BadRequest(new { error = "Body must be {\"url\": \"...\"}" });
        var error = _link.SaveOverride(request.Url);
        if (error is not null) return BadRequest(new { error });
        bool? reachable = _link.IsLinked ? await _link.CheckHealthAsync(HttpContext?.RequestAborted ?? CancellationToken.None).ConfigureAwait(false) : null;
        return Camel(new { saved = true, effectiveUrl = _link.EffectiveUrl, linked = _link.IsLinked, reachable, error = _link.Error });
    }

    [HttpGet("Plays")]
    public IActionResult Plays([FromQuery] long since = 0, [FromQuery] int? limit = null) => Page(since, limit, _plays.Since);

    [HttpGet("LibraryEvents")]
    public IActionResult LibraryEvents([FromQuery] long since = 0, [FromQuery] int? limit = null) => Page(since, limit, _annex.LibrarySince);

    [HttpGet("UserDataEvents")]
    public IActionResult UserDataEvents([FromQuery] long since = 0, [FromQuery] int? limit = null) => Page(since, limit, _annex.UserDataSince);

    [HttpGet("Logins")]
    public IActionResult Logins([FromQuery] long since = 0, [FromQuery] int? limit = null) => Page(since, limit, _annex.LoginsSince);

    /// <summary>Backup push from Jellystics: inserts missing rows as import-jellystics, never touches an existing row (so never a plugin row).</summary>
    [HttpPost("Import/Plays")]
    public async Task<IActionResult> ImportPlays()
    {
        ImportPlaysRequest? request;
        try { request = await JsonSerializer.DeserializeAsync<ImportPlaysRequest>(Request.Body, JsonOptions).ConfigureAwait(false); }
        catch (JsonException ex) { return BadRequest(new { error = "Invalid JSON: " + ex.Message }); }
        if (request?.Items is null) return BadRequest(new { error = "Body must be {\"items\": [...]}" });
        if (request.Items.Count > MaxImportItems) return BadRequest(new { error = $"At most {MaxImportItems} items per request" });

        try
        {
            var result = await _queue.RunAsync(() =>
            {
                int imported = 0, skipped = 0, rejected = 0;
                var fresh = new List<PlayRecord>();
                var seen = new HashSet<string>();
                foreach (var p in request.Items)
                {
                    if (p is null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.UserId) || string.IsNullOrWhiteSpace(p.ItemId) || p.StartedAt == default) { rejected++; continue; }
                    if (!seen.Add(p.Id) || _plays.Get(p.Id) is not null) { skipped++; continue; }
                    IdNormalizer.Normalize(p);
                    // DurationRepaired is stored as sent: only import-pbr rows are computed by the plugin.
                    p.Source = PlaySources.ImportJellystics;
                    p.State = PlayStates.Closed;
                    if (p.LastEventAt == default) p.LastEventAt = p.EndedAt ?? p.StartedAt;
                    p.EndedAt ??= p.LastEventAt;
                    p.StreamChanges ??= "[]";
                    fresh.Add(p);
                    imported++;
                }
                _plays.Upsert(fresh);
                return (imported, skipped, rejected);
            }).ConfigureAwait(false);
            return Camel(new { received = request.Items.Count, imported = result.imported, skipped = result.skipped, rejected = result.rejected });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("stopped")) { return StatusCode(503, new { error = "Jellyfin is shutting down" }); }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }

    private IActionResult Page<T>(long since, int? limit, Func<long, int, IReadOnlyList<T>> read) where T : class
    {
        var take = limit ?? DefaultLimit;
        if (take < 1 || take > MaxLimit) return BadRequest(new { error = $"limit must be between 1 and {MaxLimit}" });
        if (since < 0) return BadRequest(new { error = "since must be >= 0" });
        var rows = read(since, take + 1);
        var hasMore = rows.Count > take;
        var items = hasMore ? rows.Take(take).ToList() : rows.ToList();
        // next = Version of the last item; an empty page keeps the caller's cursor (never resets it to 0).
        var next = items.Count > 0 ? VersionOf(items[^1]) : since;
        return Camel(new { items, next, hasMore });
    }

    private static long VersionOf(object row) => row switch
    {
        PlayRecord p => p.Version,
        LibraryEventRecord l => l.Version,
        UserDataEventRecord u => u.Version,
        LoginRecord g => g.Version,
        _ => throw new InvalidOperationException("Unknown row type"),
    };

    private static ContentResult Camel(object value) => new()
    {
        Content = JsonSerializer.Serialize(value, JsonOptions),
        ContentType = "application/json",
        StatusCode = 200,
    };

    public sealed class ImportPlaysRequest
    {
        public List<PlayRecord?>? Items { get; set; }
    }

    public sealed class LinkOverrideRequest
    {
        public string? Url { get; set; }
    }

    public sealed class LinkRequest
    {
        public string? Url { get; set; }

        public string? Secret { get; set; }
    }
}

/// <summary>ISO-8601 UTC with a Z; input without an offset is read as UTC.</summary>
internal sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("A date must be an ISO-8601 string");
        var s = reader.GetString();
        if (!DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var value))
            throw new JsonException($"Invalid date \"{s}\"");
        return value.ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
}

internal sealed class NullableUtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    private static readonly UtcDateTimeOffsetConverter Inner = new();

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(DateTimeOffset), options);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue(); else Inner.Write(writer, value.Value, options);
    }
}
