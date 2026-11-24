namespace Jellyfin.Plugin.Jellystics.Me;

/// <summary>Where the link to Jellystics is persisted (the plugin configuration in production, memory in tests).</summary>
public interface IMeSettings
{
    string Url { get; }

    string Secret { get; }

    string UrlOverride { get; }

    void SaveLink(string url, string secret);
}

/// <summary>Reads and writes the link in the plugin XML configuration.</summary>
public sealed class PluginMeSettings : IMeSettings
{
    private static Plugin? P => Plugin.Instance;

    public string Url => P?.Configuration.JellysticsUrl ?? string.Empty;

    public string Secret => P?.Configuration.JellysticsSecret ?? string.Empty;

    public string UrlOverride => P?.Configuration.JellysticsUrlOverride ?? string.Empty;

    public void SaveLink(string url, string secret)
    {
        var plugin = P ?? throw new InvalidOperationException("The plugin is not loaded");
        plugin.Configuration.JellysticsUrl = url;
        plugin.Configuration.JellysticsSecret = secret;
        plugin.SaveConfiguration();
    }
}
