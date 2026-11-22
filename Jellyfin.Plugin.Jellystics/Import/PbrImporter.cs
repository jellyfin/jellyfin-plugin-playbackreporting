using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Jellystics.Models;
using Jellyfin.Plugin.Jellystics.Storage;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Jellystics.Import;

public sealed record ItemInfo(string ItemType, long? RunTimeTicks, string? SeriesId, string? SeriesName, string? SeasonId, string? AlbumId, string? AlbumName, int? ParentIndexNumber, int? IndexNumber, string? LibraryId);

/// <summary>Enrichment source for imported rows (behind an interface so the importer is testable).</summary>
public interface IItemLookup
{
    ItemInfo? Find(string itemId);
}

public sealed class LibraryItemLookup : IItemLookup
{
    private readonly ILibraryManager _library;

    public LibraryItemLookup(ILibraryManager library) => _library = library;

    public ItemInfo? Find(string itemId)
    {
        if (!Guid.TryParse(itemId, out var id)) return null;
        var item = _library.GetItemById(id);
        if (item is null) return null;
        string? seriesId = null, seriesName = null, seasonId = null, albumId = null, albumName = null;
        if (item is Episode ep) { seriesId = ep.SeriesId.ToString("N"); seriesName = ep.SeriesName; seasonId = ep.SeasonId.ToString("N"); }
        if (item is Audio au) { albumId = au.AlbumEntity?.Id.ToString("N"); albumName = au.Album; }
        return new ItemInfo(
            item.GetBaseItemKind().ToString(), item.RunTimeTicks, seriesId, seriesName, seasonId, albumId, albumName,
            item.ParentIndexNumber, item.IndexNumber, _library.GetCollectionFolders(item).FirstOrDefault()?.Id.ToString("N"));
    }
}

public sealed record PbrImportResult(int Read, int Imported, int Skipped, int Repaired);

/// <summary>Imports the official Playback Reporting history (playback_reporting.db, read-only) as closed plays.</summary>
public sealed class PbrImporter
{
    private const int BatchSize = 500;
    private static readonly Regex MethodPattern = new(@"^(\w+)(?: \(v:([^\s)]*) a:([^\s)]*)\))?$", RegexOptions.Compiled);

    private readonly PlayStore _store;
    private readonly IItemLookup _items;
    private readonly TimeZoneInfo _serverZone;
    private static readonly string[] OffsetFormats = { "yyyy-MM-dd HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK" };
    private static readonly string[] LocalFormats = { "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF" };

    public PbrImporter(PlayStore store, IItemLookup items, TimeZoneInfo serverZone)
    {
        _store = store; _items = items; _serverZone = serverZone;
    }

    /// <remarks>
    /// Writes its batches straight to the store, deliberately outside the WriteQueue (a long import must not starve live plays);
    /// the single SQLite writer lock is shared through the connection busy_timeout, so both sides wait for each other briefly.
    /// </remarks>
    private readonly record struct Row(long RowId, string? Date, string User, string Item, string? ItemType, string? ItemName, string? Method, string? Client, string? Device, long Seconds);

    public PbrImportResult Import(string dbPath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        int read = 0, imported = 0, skipped = 0, repaired = 0;
        var cache = new Dictionary<string, ItemInfo?>();
        var chunk = new List<Row>(BatchSize);

        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT rowid, DateCreated, UserId, ItemId, ItemType, ItemName, PlaybackMethod, ClientName, DeviceName, PlayDuration FROM PlaybackActivity ORDER BY rowid";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            read++;
            try
            {
                chunk.Add(new Row(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? string.Empty : r.GetString(2), r.IsDBNull(3) ? string.Empty : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                    r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? 0 : r.GetInt64(9)));
            }
            catch (InvalidCastException) { skipped++; }
            if (chunk.Count >= BatchSize) { Flush(); progress?.Report(read); }
        }
        Flush();
        progress?.Report(read);
        return new PbrImportResult(read, imported, skipped, repaired);

        void Flush()
        {
            if (chunk.Count == 0) return;
            // One IN query for the whole chunk: re-imports leave existing rows alone (an upsert would bump their Version and make sync consumers re-read them).
            var existing = _store.ExistingIds(chunk.Select(x => "pbr-" + x.RowId));
            var unflagged = _store.IdsWithoutRepairFlag(existing);   // rows imported before schema v2: fill the flag only
            var flags = new Dictionary<string, bool>();
            var batch = new List<PlayRecord>(chunk.Count);
            foreach (var row in chunk)
            {
                try
                {
                    var id = "pbr-" + row.RowId;
                    if (existing.Contains(id))
                    {
                        if (unflagged.Contains(id))
                        {
                            var known = NormalizeId(row.Item);
                            if (!cache.TryGetValue(known, out var knownInfo)) cache[known] = knownInfo = _items.Find(known);
                            flags[id] = DurationRepair.RepairSeconds(row.Seconds, knownInfo?.RunTimeTicks) != row.Seconds;
                        }
                        skipped++; continue;
                    }
                    var userId = NormalizeId(row.User);
                    var itemId = NormalizeId(row.Item);
                    var started = ParseStart(row.Date ?? throw new FormatException("no date")).AddSeconds(-20);

                    if (!cache.TryGetValue(itemId, out var info)) cache[itemId] = info = _items.Find(itemId);
                    var seconds = DurationRepair.RepairSeconds(row.Seconds, info?.RunTimeTicks);
                    var end = started.AddSeconds(seconds);

                    if (_store.ExistsOverlapping(userId, itemId, started, end, PlaySources.Plugin)) { skipped++; continue; }

                    if (seconds != row.Seconds) repaired++;
                    var play = new PlayRecord
                    {
                        Id = id, UserId = userId, ItemId = itemId, ItemName = row.ItemName,
                        ItemType = info?.ItemType ?? row.ItemType, RunTimeTicks = info?.RunTimeTicks,
                        SeriesId = info?.SeriesId, SeriesName = info?.SeriesName, SeasonId = info?.SeasonId, AlbumId = info?.AlbumId, AlbumName = info?.AlbumName,
                        ParentIndexNumber = info?.ParentIndexNumber, IndexNumber = info?.IndexNumber, LibraryId = info?.LibraryId,
                        StartedAt = started, EndedAt = end, LastEventAt = end, WatchedMs = seconds * 1000,
                        Client = row.Client, DeviceName = row.Device,
                        State = PlayStates.Closed, Source = PlaySources.ImportPbr, DurationRepaired = seconds != row.Seconds,
                    };
                    ApplyMethod(play, row.Method);
                    batch.Add(play);
                    imported++;
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
                {
                    skipped++;   // a malformed row must not abort the whole import
                }
            }
            _store.Upsert(batch);
            _store.SetDurationRepaired(flags);
            chunk.Clear();
        }
    }

    private static string NormalizeId(string id) => IdNormalizer.Normalize(id)!;

    /// <summary>PBR stores <c>DateTime.Now</c> of the server (local, "yyyy-MM-dd HH:mm:ss.FFFFFFF"); values carrying an offset or Z are converted directly.</summary>
    private DateTimeOffset ParseStart(string value)
    {
        var s = value.Trim();
        if (DateTimeOffset.TryParseExact(s, OffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset) && (s.EndsWith('Z') || s.Contains('+') || s.LastIndexOf('-') > 10))
            return withOffset.ToUniversalTime();
        var local = DateTime.SpecifyKind(DateTime.ParseExact(s, LocalFormats.Append("yyyy-MM-dd HH:mm:ss").ToArray(), CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Unspecified);
        if (_serverZone.IsInvalidTime(local)) local = local.AddHours(1);   // clock gap of a DST change
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _serverZone), TimeSpan.Zero);
    }

    private static string? Codec(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("direct", StringComparison.OrdinalIgnoreCase) || value.Equals("na", StringComparison.OrdinalIgnoreCase) ? null : value;

    private static void ApplyMethod(PlayRecord play, string? method)
    {
        if (string.IsNullOrWhiteSpace(method) || method.Trim().Equals("na", StringComparison.OrdinalIgnoreCase)) return;
        var m = MethodPattern.Match(method.Trim());
        if (!m.Success) { play.PlayMethod = method.Trim(); return; }
        play.PlayMethod = m.Groups[1].Value;
        if (m.Groups[2].Success) play.VideoCodecTo = Codec(m.Groups[2].Value);
        if (m.Groups[3].Success) play.AudioCodecTo = Codec(m.Groups[3].Value);
    }
}
