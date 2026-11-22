using Jellyfin.Plugin.Jellystics.Collectors;
using Jellyfin.Plugin.Jellystics.Storage;
using Jellyfin.Plugin.Jellystics.Tracking;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Authentication;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jellystics;

/// <summary>Registers the plugin services.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IClock, SystemClock>();
        serviceCollection.AddSingleton(_ =>
        {
            var db = new JellysticsDb(Path.Combine(Plugin.Instance!.DataFolderPath, "jellystics.db"));
            db.Migrate();
            return db;
        });
        serviceCollection.AddSingleton<PlayStore>();
        serviceCollection.AddSingleton<PlaySessionRegistry>();
        serviceCollection.AddSingleton<PlayRecordFactory>();
        serviceCollection.AddSingleton<WriteQueue>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<WriteQueue>());
        serviceCollection.AddHostedService<PlaybackCollector>();
        serviceCollection.AddSingleton<AnnexStore>();
        serviceCollection.AddHostedService<LibraryCollector>();
        serviceCollection.AddHostedService<UserDataCollector>();
        serviceCollection.AddScoped<IEventConsumer<AuthenticationResultEventArgs>, LoginSucceededConsumer>();
        serviceCollection.AddScoped<IEventConsumer<AuthenticationRequestEventArgs>, LoginFailedConsumer>();
    }
}
