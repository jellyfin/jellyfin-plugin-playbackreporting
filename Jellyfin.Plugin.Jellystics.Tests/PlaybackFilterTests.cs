using Jellyfin.Plugin.Jellystics.Collectors;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class PlaybackFilterTests
{
    private static PlaybackProgressEventArgs Args(Movie? item, bool automated = false, bool users = true)
    {
        var e = new PlaybackProgressEventArgs { Item = item, IsAutomated = automated };
        if (users) e.Users.Add(new Jellyfin.Database.Implementations.Entities.User("u", "default", "default"));
        return e;
    }

    [Fact] public void TracksANormalPlay() => Assert.True(PlaybackCollector.ShouldTrack(Args(new Movie())));
    [Fact] public void IgnoresAutomatedProgress() => Assert.False(PlaybackCollector.ShouldTrack(Args(new Movie(), automated: true)));
    [Fact] public void IgnoresExtras() => Assert.False(PlaybackCollector.ShouldTrack(Args(new Movie { ExtraType = MediaBrowser.Model.Entities.ExtraType.Trailer })));
    [Fact] public void IgnoresEventsWithoutItemOrUser()
    {
        Assert.False(PlaybackCollector.ShouldTrack(Args(null)));
        Assert.False(PlaybackCollector.ShouldTrack(Args(new Movie(), users: false)));
    }
}
