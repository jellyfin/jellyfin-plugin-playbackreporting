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
    private static readonly Regex MethodPattern = new(@"^(\w+)(?: \(v:([^\s)]+) a:([^\s)]+)\))?$", RegexOptions.Compiled);

    private readonly PlayStore _store;
    private readonly IItemLookup _items;
    private readonly TimeZoneInfo _serverZone;

    public PbrImporter(PlayStore store, IItemLookup items, TimeZoneInfo serverZone)
    {
        _store = store; _items = items; _serverZone = serverZone;
    }

    public PbrImportResult Import(string dbPath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        int read = 0, imported = 0, skipped = 0, repaired = 0;
        var batch = new List<PlayRecord>(BatchSize);

        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT rowid, DateCreated, UserId, ItemId, ItemType, ItemName, PlaybackMethod, ClientName, DeviceName, PlayDuration FROM PlaybackActivity ORDER BY rowid";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            read++;
            var rowId = r.GetInt64(0);
            var userId = r.IsDBNull(2) ? string.Empty : r.GetString(2);
            var itemId = r.IsDBNull(3) ? string.Empty : r.GetString(3);
            var local = DateTime.SpecifyKind(DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Unspecified);
            var started = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _serverZone), TimeSpan.Zero).AddSeconds(-20);

            var info = _items.Find(itemId);
            var rawSeconds = r.IsDBNull(9) ? 0 : r.GetInt64(9);
            var seconds = DurationRepair.RepairSeconds(rawSeconds, info?.RunTimeTicks);
            if (seconds != rawSeconds) repaired++;
            var end = started.AddSeconds(seconds);

            if (_store.ExistsOverlapping(userId, itemId, started, end, PlaySources.Plugin)) { skipped++; }
            else
            {
                var play = new PlayRecord
                {
                    Id = "pbr-" + rowId, UserId = userId, ItemId = itemId, ItemName = r.IsDBNull(5) ? null : r.GetString(5),
                    ItemType = info?.ItemType ?? (r.IsDBNull(4) ? null : r.GetString(4)), RunTimeTicks = info?.RunTimeTicks,
                    SeriesId = info?.SeriesId, SeriesName = info?.SeriesName, SeasonId = info?.SeasonId, AlbumId = info?.AlbumId, AlbumName = info?.AlbumName,
                    ParentIndexNumber = info?.ParentIndexNumber, IndexNumber = info?.IndexNumber, LibraryId = info?.LibraryId,
                    StartedAt = started, EndedAt = end, LastEventAt = end, WatchedMs = seconds * 1000,
                    Client = r.IsDBNull(7) ? null : r.GetString(7), DeviceName = r.IsDBNull(8) ? null : r.GetString(8),
                    State = PlayStates.Closed, Source = PlaySources.ImportPbr,
                };
                ApplyMethod(play, r.IsDBNull(6) ? null : r.GetString(6));
                batch.Add(play);
                imported++;
                if (batch.Count >= BatchSize) { _store.Upsert(batch); batch.Clear(); }
            }
            if (read % 100 == 0) progress?.Report(read);
        }
        if (batch.Count > 0) _store.Upsert(batch);
        progress?.Report(read);
        return new PbrImportResult(read, imported, skipped, repaired);
    }

    private static void ApplyMethod(PlayRecord play, string? method)
    {
        if (string.IsNullOrWhiteSpace(method)) return;
        var m = MethodPattern.Match(method.Trim());
        if (!m.Success) { play.PlayMethod = method.Trim(); return; }
        play.PlayMethod = m.Groups[1].Value;
        if (m.Groups[2].Success && !m.Groups[2].Value.Equals("direct", StringComparison.OrdinalIgnoreCase)) play.VideoCodecTo = m.Groups[2].Value;
        if (m.Groups[3].Success && !m.Groups[3].Value.Equals("direct", StringComparison.OrdinalIgnoreCase)) play.AudioCodecTo = m.Groups[3].Value;
    }
}
