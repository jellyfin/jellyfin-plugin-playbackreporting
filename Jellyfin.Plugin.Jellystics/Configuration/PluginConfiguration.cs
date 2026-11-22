using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Jellystics.Configuration;

/// <summary>Jellystics plugin settings.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the path of the official Playback Reporting DB; empty = DataPath/playback_reporting.db.</summary>
    public string PbrDbPath { get; set; } = string.Empty;
}
