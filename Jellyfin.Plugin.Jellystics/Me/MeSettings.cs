using Jellyfin.Plugin.Jellystics.Configuration;

namespace Jellyfin.Plugin.Jellystics.Me;

/// <summary>Where the link to Jellystics is persisted (the plugin configuration in production, memory in tests).</summary>
public interface IMeSettings
{
    string Url { get; }

    string Secret { get; }

    string UrlOverride { get; }

    void SaveLink(string url, string secret);

    void SaveOverride(string url);
}

/// <summary>
/// Reads and writes the link in the plugin XML configuration. Each save changes only its own fields on the live
/// configuration object; the generic Jellyfin configuration endpoint cannot change them (see Plugin.UpdateConfiguration).
/// </summary>
public sealed class PluginMeSettings : IMeSettings
{
    internal static readonly object Gate = new();

    private static Plugin? P => Plugin.Instance;

    public string Url => P?.Configuration.JellysticsUrl ?? string.Empty;

    public string Secret => P?.Configuration.JellysticsSecret ?? string.Empty;

    public string UrlOverride => P?.Configuration.JellysticsUrlOverride ?? string.Empty;

    public void SaveLink(string url, string secret) => Mutate(c => { c.JellysticsUrl = url; c.JellysticsSecret = secret; });

    public void SaveOverride(string url) => Mutate(c => c.JellysticsUrlOverride = url);

    private static void Mutate(Action<PluginConfiguration> change)
    {
        var plugin = P ?? throw new InvalidOperationException("The plugin is not loaded");
        lock (Gate)
        {
            change(plugin.Configuration);
            plugin.SaveConfiguration();
        }
    }
}
