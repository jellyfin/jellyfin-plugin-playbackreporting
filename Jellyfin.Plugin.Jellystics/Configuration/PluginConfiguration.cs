using System.Text.Json.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Jellystics.Configuration;

/// <summary>Jellystics plugin settings.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the path of the official Playback Reporting DB; empty = DataPath/playback_reporting.db.</summary>
    public string PbrDbPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellystics base URL pushed by Jellystics through POST /Jellystics/Link.</summary>
    public string JellysticsUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the shared secret pushed by Jellystics, sent as X-Jellystics-Secret to /api/me. Persisted in the XML,
    /// never returned by Jellyfin's JSON configuration API (GET /Plugins/{id}/Configuration).
    /// </summary>
    [JsonIgnore]
    public string JellysticsSecret { get; set; } = string.Empty;

    /// <summary>Gets or sets an admin override of the Jellystics URL (POST /Jellystics/LinkOverride); when set it wins over <see cref="JellysticsUrl"/>.</summary>
    public string JellysticsUrlOverride { get; set; } = string.Empty;

    /// <summary>
    /// Keeps the link fields of <paramref name="current"/> on a configuration posted through Jellyfin's generic
    /// endpoint: they only change through /Jellystics/Link and /Jellystics/LinkOverride, so a config page that read
    /// the configuration before a Link push can never write an old secret back.
    /// </summary>
    public static void KeepLinkFields(PluginConfiguration incoming, PluginConfiguration current)
    {
        incoming.JellysticsUrl = current.JellysticsUrl;
        incoming.JellysticsSecret = current.JellysticsSecret;
        incoming.JellysticsUrlOverride = current.JellysticsUrlOverride;
    }
}
