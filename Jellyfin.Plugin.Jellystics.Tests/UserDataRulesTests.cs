using Jellyfin.Plugin.Jellystics.Collectors;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class UserDataRulesTests
{
    private static string[] D(UserDataSaveReason r, bool played, bool fav, (bool Played, bool Fav)? before) =>
        UserDataRules.Decide(r, played, fav, before is not null, before ?? default).ToArray();

    [Fact]
    public void ResumableStartThenUnfinishedIsNothing()
    {
        Assert.Empty(D(UserDataSaveReason.PlaybackStart, false, false, (false, false)));
        Assert.Empty(D(UserDataSaveReason.PlaybackFinished, false, false, (false, false)));
    }

    [Fact]
    public void StartThenFinishedPlayedEmitsPlayedOnce()
    {
        Assert.Equal(new[] { "played" }, D(UserDataSaveReason.PlaybackFinished, true, false, (false, false)));
        Assert.Empty(D(UserDataSaveReason.PlaybackFinished, true, false, (true, false)));
    }

    [Fact]
    public void AudioStartWithPlayedTrueEmitsPlayed() =>
        Assert.Equal(new[] { "played" }, D(UserDataSaveReason.PlaybackStart, true, false, (false, false)));

    [Fact]
    public void FavouriteOnAndOffViaUpdateUserRating()
    {
        Assert.Equal(new[] { "favorite" }, D(UserDataSaveReason.UpdateUserRating, false, true, (false, false)));
        Assert.Equal(new[] { "unfavorite" }, D(UserDataSaveReason.UpdateUserRating, false, false, (false, true)));
        Assert.Empty(D(UserDataSaveReason.UpdateUserRating, false, true, (false, true)));
    }

    [Fact]
    public void FavouriteOnCacheMissIsAStateAssertion() =>
        Assert.Equal(new[] { "favorite" }, D(UserDataSaveReason.UpdateUserRating, false, true, null));

    [Fact]
    public void FavouriteViaUpdateUserDataOnlyOnChange()
    {
        Assert.Equal(new[] { "favorite" }, D(UserDataSaveReason.UpdateUserData, false, true, (false, false)));
        Assert.Empty(D(UserDataSaveReason.UpdateUserData, false, false, (false, false)));
    }

    [Fact]
    public void TogglePlayedOnAlreadyPlayedIsNothing()
    {
        Assert.Empty(D(UserDataSaveReason.TogglePlayed, true, false, (true, false)));
        Assert.Equal(new[] { "played" }, D(UserDataSaveReason.TogglePlayed, true, false, (false, false)));
        Assert.Equal(new[] { "unplayed" }, D(UserDataSaveReason.TogglePlayed, false, false, (true, false)));
        Assert.Empty(D(UserDataSaveReason.TogglePlayed, false, false, (false, false)));
    }

    [Fact]
    public void ProgressNeverEmits() => Assert.Empty(D(UserDataSaveReason.PlaybackProgress, true, true, (false, false)));

    [Fact]
    public void ReplayOfAPlayedResumableItemEmitsUnplayedAtStartThenPlayedAtFinish()
    {
        // Jellyfin 10.11: Start saves Played=false for resumable video, Finished saves Played=true.
        Assert.Equal(new[] { "unplayed" }, D(UserDataSaveReason.PlaybackStart, false, false, (true, false)));
        Assert.Equal(new[] { "played" }, D(UserDataSaveReason.PlaybackFinished, true, false, (false, false)));
        Assert.Empty(D(UserDataSaveReason.PlaybackStart, false, false, null));   // unknown prior state: stay silent
        Assert.Empty(D(UserDataSaveReason.PlaybackProgress, false, false, (true, false)));
        Assert.Empty(D(UserDataSaveReason.PlaybackFinished, false, false, (true, false)));
    }
}
