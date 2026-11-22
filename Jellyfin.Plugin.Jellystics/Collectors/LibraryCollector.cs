using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
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
    private const string SnapshotFlag = "catalog_snapshot_done";
    private const int PageSize = 1000;
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
        try { if (_store.GetMeta(SnapshotFlag) is null) _ = Task.Run(Snapshot); }   // off the startup path; only batch writes go through the queue
        catch (Exception ex) { _logger.LogError(ex, "Jellystics catalog snapshot could not start"); }   // never keep Jellyfin from starting
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
        var rec = ToRecord(e.Item, "added", _clock.UtcNow);   // live addition: At is when we saw it, DateCreated is Jellyfin's
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

    /// <summary>
    /// First-run catalog snapshot: runs on a background task (reads), pages the library by 1000 and enqueues each batch as a single write.
    /// The "done" flag is persisted only after every batch was written, so an interrupted snapshot is redone (already present items are skipped).
    /// </summary>
    private void Snapshot()
    {
        try
        {
            var allRoots = _library.GetUserRootFolder().Children.OfType<Folder>().ToList();   // computed once: GetCollectionFolders is costly per item
            var total = 0;
            var failed = false;
            for (var start = 0; ; start += PageSize)
            {
                var page = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = Kept, Recursive = true, IsVirtualItem = false,   // HasOwnerId=false returns nothing in 10.11: extras are filtered by IsKept (ExtraType)
                   
                    StartIndex = start, Limit = PageSize, DtoOptions = new DtoOptions(false),
                    OrderBy = new[] { (ItemSortBy.DateCreated, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending), (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) },
                });
                if (page.Count == 0) break;
                var snapshotAt = _clock.UtcNow;
                var recs = page.Where(IsKept).Select(i => ToRecord(i, "present", snapshotAt, allRoots)).ToList();
                total += recs.Count;
                if (!_queue.Enqueue(() =>
                {
                    try { _store.UpsertLibrary(recs); }
                    catch (Exception ex) { failed = true; _logger.LogError(ex, "Jellystics catalog snapshot batch failed"); }
                })) return;   // shutting down: the flag stays unset, the snapshot will rerun
                if (page.Count < PageSize) break;
            }

            _queue.Enqueue(() =>
            {
                if (failed) return;
                _store.SetMeta(SnapshotFlag, "1");
                _logger.LogInformation("Jellystics catalog snapshot: {Count} items", total);
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Jellystics catalog snapshot failed"); }
    }

    private static bool IsKept(BaseItem item) => !item.IsVirtualItem && item.ExtraType is null && Kept.Contains(item.GetBaseItemKind());

    private static DateTimeOffset At(BaseItem item) => new(DateTime.SpecifyKind(item.DateCreated, DateTimeKind.Utc));

    private LibraryEventRecord ToRecord(BaseItem item, string kind, DateTimeOffset at, IEnumerable<Folder>? allRoots = null)
    {
        var id = item.Id.ToString("N");
        var rec = new LibraryEventRecord
        {
            Id = (kind == "present" ? "present-" : "add-") + id, Kind = kind, At = at, DateCreated = kind == "removed" ? null : At(item), ItemId = id, ItemType = item.GetBaseItemKind().ToString(), ItemName = item.Name,
            RunTimeTicks = item.RunTimeTicks, SizeBytes = item.Size,
            LibraryId = (allRoots is null ? _library.GetCollectionFolders(item) : _library.GetCollectionFolders(item, allRoots)).FirstOrDefault()?.Id.ToString("N"),
        };
        if (item is Episode ep) rec.SeriesId = ep.SeriesId == Guid.Empty ? null : ep.SeriesId.ToString("N");
        if (item is Audio au) rec.AlbumId = au.AlbumEntity is { } album ? album.Id.ToString("N") : null;
        return rec;
    }

    private void Safe(Action a)
    {
        try { a(); } catch (Exception ex) { _logger.LogError(ex, "Jellystics library event failed"); }
    }
}
