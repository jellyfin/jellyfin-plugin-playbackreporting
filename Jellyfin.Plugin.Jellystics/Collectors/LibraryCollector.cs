using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jellystics.Models;
using Jellyfin.Plugin.Jellystics.Storage;
using Jellyfin.Plugin.Jellystics.Tracking;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellystics.Collectors;

/// <summary>Records catalog additions/removals; takes a one-off snapshot of the existing library on first run.</summary>
public sealed class LibraryCollector : IHostedService
{
    private static readonly BaseItemKind[] Kept = { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Series, BaseItemKind.MusicAlbum };

    private readonly ILibraryManager _library;
    private readonly AnnexStore _store;
    private readonly WriteQueue _queue;
    private readonly IClock _clock;
    private readonly ILogger<LibraryCollector> _logger;

    public LibraryCollector(ILibraryManager library, AnnexStore store, WriteQueue queue, IClock clock, ILogger<LibraryCollector> logger)
    {
        _library = library; _store = store; _queue = queue; _clock = clock; _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += OnAdded;
        _library.ItemRemoved += OnRemoved;
        _queue.Enqueue(Snapshot);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnAdded;
        _library.ItemRemoved -= OnRemoved;
        return Task.CompletedTask;
    }

    private void OnAdded(object? sender, ItemChangeEventArgs e) => Safe(() =>
    {
        if (!IsKept(e.Item)) return;
        var rec = ToRecord(e.Item, "added", At(e.Item));
        _queue.Enqueue(() => _store.UpsertLibrary(new[] { rec }));
    });

    private void OnRemoved(object? sender, ItemChangeEventArgs e) => Safe(() =>
    {
        if (!IsKept(e.Item)) return;
        var now = _clock.UtcNow;
        var rec = ToRecord(e.Item, "removed", now);
        rec.Id = $"rm-{rec.ItemId}-{now:yyyyMMddHHmmss}";
        _queue.Enqueue(() => _store.UpsertLibrary(new[] { rec }));
    });

    /// <summary>Runs on the write queue: first-run catalog snapshot, in batches of 500.</summary>
    private void Snapshot()
    {
        try
        {
            if (_store.CountLibrary() > 0) return;
            var items = _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = Kept, Recursive = true, IsVirtualItem = false });
            var total = 0;
            foreach (var batch in items.Where(IsKept).Chunk(500))
            {
                _store.UpsertLibrary(batch.Select(i => ToRecord(i, "added", At(i))).ToList());
                total += batch.Length;
            }
            _logger.LogInformation("Jellystics catalog snapshot: {Count} items", total);
        }
        catch (Exception ex) { _logger.LogError(ex, "Jellystics catalog snapshot failed"); }
    }

    private static bool IsKept(BaseItem item) => !item.IsVirtualItem && Kept.Contains(item.GetBaseItemKind());

    private static DateTimeOffset At(BaseItem item) => new(DateTime.SpecifyKind(item.DateCreated, DateTimeKind.Utc));

    private LibraryEventRecord ToRecord(BaseItem item, string kind, DateTimeOffset at)
    {
        var id = item.Id.ToString("N");
        var rec = new LibraryEventRecord
        {
            Id = "add-" + id, Kind = kind, At = at, ItemId = id, ItemType = item.GetBaseItemKind().ToString(), ItemName = item.Name,
            RunTimeTicks = item.RunTimeTicks, SizeBytes = item.Size,
            LibraryId = _library.GetCollectionFolders(item).FirstOrDefault()?.Id.ToString("N"),
        };
        if (item is Episode ep) rec.SeriesId = ep.SeriesId.ToString("N");
        if (item is Audio au) rec.AlbumId = au.AlbumEntity?.Id.ToString("N");
        return rec;
    }

    private void Safe(Action a)
    {
        try { a(); } catch (Exception ex) { _logger.LogError(ex, "Jellystics library event failed"); }
    }
}
