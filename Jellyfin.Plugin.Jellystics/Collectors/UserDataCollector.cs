using System.Collections.Concurrent;
using Jellyfin.Plugin.Jellystics.Models;
using Jellyfin.Plugin.Jellystics.Storage;
using Jellyfin.Plugin.Jellystics.Tracking;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellystics.Collectors;

/// <summary>Records played / unplayed / favorite / unfavorite marks.</summary>
public sealed class UserDataCollector : IHostedService
{
    private readonly IUserDataManager _userData;
    private readonly AnnexStore _store;
    private readonly WriteQueue _queue;
    private readonly IClock _clock;
    private readonly ILogger<UserDataCollector> _logger;
    private readonly ConcurrentDictionary<(Guid User, Guid Item), (bool Played, bool Fav)> _cache = new();

    public UserDataCollector(IUserDataManager userData, AnnexStore store, WriteQueue queue, IClock clock, ILogger<UserDataCollector> logger)
    {
        _userData = userData; _store = store; _queue = queue; _clock = clock; _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) { _userData.UserDataSaved += OnSaved; return Task.CompletedTask; }

    public Task StopAsync(CancellationToken cancellationToken) { _userData.UserDataSaved -= OnSaved; return Task.CompletedTask; }

    private void OnSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            var key = (e.UserId, e.Item.Id);
            var known = _cache.TryGetValue(key, out var before);
            var played = e.UserData.Played;
            var fav = e.UserData.IsFavorite;
            _cache[key] = (played, fav);

            var kinds = new List<string>();
            switch (e.SaveReason)
            {
                case UserDataSaveReason.TogglePlayed:
                    kinds.Add(played ? "played" : "unplayed");
                    break;
                case UserDataSaveReason.PlaybackFinished:
                    if (!known || !before.Played) kinds.Add("played");
                    break;
                case UserDataSaveReason.UpdateUserData:
                    if (known ? fav != before.Fav : fav) kinds.Add(fav ? "favorite" : "unfavorite");
                    break;
                case UserDataSaveReason.Import:
                    if (played) kinds.Add("played");
                    break;
            }

            if (kinds.Count == 0) return;
            var at = _clock.UtcNow;
            var recs = kinds.Select(k => new UserDataEventRecord
            {
                Id = $"ud-{e.UserId:N}-{e.Item.Id:N}-{k}-{at.UtcTicks}", At = at, UserId = e.UserId.ToString("N"), ItemId = e.Item.Id.ToString("N"),
                Kind = k, Reason = e.SaveReason.ToString(),
            }).ToList();
            _queue.Enqueue(() => _store.UpsertUserData(recs));
        }
        catch (Exception ex) { _logger.LogError(ex, "Jellystics user data event failed"); }
    }
}
