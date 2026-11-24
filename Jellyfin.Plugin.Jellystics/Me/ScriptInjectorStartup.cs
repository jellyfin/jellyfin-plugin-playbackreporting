using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.Jellystics.Me;

/// <summary>Adds <see cref="ScriptInjectorMiddleware"/> at the start of Jellyfin's pipeline.</summary>
public sealed class ScriptInjectorStartup : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<ScriptInjectorMiddleware>();
        next(app);
    };
}
