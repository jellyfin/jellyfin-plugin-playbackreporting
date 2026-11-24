using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Jellystics.Configuration;

/// <summary>Jellystics plugin settings.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the path of the official Playback Reporting DB; empty = DataPath/playback_reporting.db.</summary>
    public string PbrDbPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellystics base URL pushed by Jellystics through POST /Jellystics/Link.</summary>
    public string JellysticsUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the shared secret pushed by Jellystics, sent as X-Jellystics-Secret to /api/me.</summary>
    public string JellysticsSecret { get; set; } = string.Empty;

    /// <summary>Gets or sets an admin override of the Jellystics URL; when set it wins over <see cref="JellysticsUrl"/>.</summary>
    public string JellysticsUrlOverride { get; set; } = string.Empty;
}
