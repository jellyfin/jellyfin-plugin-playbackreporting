using Jellyfin.Plugin.Jellystics.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Jellystics;

/// <summary>Jellystics: precise playback collector and backup for Jellystics.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        AppDataPath = applicationPaths.DataPath;
    }

    public override string Name => "Jellystics";
    public const string PluginGuid = "7f3c2a1e-9b84-4d6f-a2c5-3e1b8d9f6a40";
    public override Guid Id => new(PluginGuid);
    public override string Description => "Precise playback collector and backup for Jellystics.";
    public static Plugin? Instance { get; private set; }
    /// <summary>Gets Jellyfin's data directory (where the official playback_reporting.db lives).</summary>
    public string AppDataPath { get; }

    /// <summary>Generic configuration saves never change the link fields (see PluginConfiguration.KeepLinkFields).</summary>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        lock (Me.PluginMeSettings.Gate)
        {
            if (configuration is PluginConfiguration incoming) PluginConfiguration.KeepLinkFields(incoming, Configuration);
            base.UpdateConfiguration(configuration);
        }
    }

    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo { Name = "Jellystics", DisplayName = "Jellystics", EnableInMainMenu = true, MenuSection = "server", MenuIcon = "insights", EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html" },
        new PluginPageInfo { Name = "JellysticsConfig", EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js" },
    };
}
