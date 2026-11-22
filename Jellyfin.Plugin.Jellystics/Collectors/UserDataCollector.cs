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

/// <summary>
/// Decides which marks a UserDataSaved event represents, from Jellyfin 10.11's real sequences:
/// PlaybackStart/Progress/Finished carry the played flag as saved (Start(Played=false) for resumable video),
/// TogglePlayed fires for the mark button, favourites arrive as UpdateUserRating (UserFavoriteItems API) or UpdateUserData.
/// The cache holds the last seen (played, favourite) per user and item; a cache miss on UpdateUserRating is treated
/// as a state assertion (we cannot tell a change from a repeat), on other reasons we stay silent unless the state is positive.
/// </summary>
internal static class UserDataRules
{
    public static IEnumerable<string> Decide(UserDataSaveReason reason, bool played, bool fav, bool known, (bool Played, bool Fav) before)
    {
        switch (reason)
        {
            case UserDataSaveReason.PlaybackFinished:
                if (played && (!known || !before.Played)) yield return "played";
                break;
            case UserDataSaveReason.PlaybackStart:
                if (played && (!known || !before.Played)) yield return "played";
                // 10.11 saves Played=false at the start of a resumable item: a known-played item becoming unplayed is a real state change.
                else if (!played && known && before.Played) yield return "unplayed";
                break;
            case UserDataSaveReason.TogglePlayed:
                if (played && (!known || !before.Played)) yield return "played";
                else if (!played && (!known || before.Played)) yield return "unplayed";
                break;
            case UserDataSaveReason.UpdateUserRating:
                if (known ? fav != before.Fav : true) yield return fav ? "favorite" : "unfavorite";
                break;
            case UserDataSaveReason.UpdateUserData:
                if (known)
                {
                    if (played && !before.Played) yield return "played";
                    else if (!played && before.Played) yield return "unplayed";
                    if (fav != before.Fav) yield return fav ? "favorite" : "unfavorite";
                }
                else if (fav) yield return "favorite";
                break;
        }
    }
}
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

            var kinds = UserDataRules.Decide(e.SaveReason, played, fav, known, before).ToList();
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
