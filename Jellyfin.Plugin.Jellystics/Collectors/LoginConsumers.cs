using Jellyfin.Plugin.Jellystics.Models;
using Jellyfin.Plugin.Jellystics.Storage;
using Jellyfin.Plugin.Jellystics.Tracking;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Authentication;

namespace Jellyfin.Plugin.Jellystics.Collectors;

public sealed class LoginSucceededConsumer : IEventConsumer<AuthenticationResultEventArgs>
{
    private readonly AnnexStore _store; private readonly WriteQueue _queue; private readonly IClock _clock;
    public LoginSucceededConsumer(AnnexStore store, WriteQueue queue, IClock clock) { _store = store; _queue = queue; _clock = clock; }

    public Task OnEvent(AuthenticationResultEventArgs e)
    {
        var at = _clock.UtcNow; var s = e.SessionInfo;
        var rec = new LoginRecord
        {
            Id = $"login-{at.UtcTicks}-{s?.DeviceId}", At = at, UserId = e.User?.Id.ToString("N"), UserName = e.User?.Name,
            DeviceId = s?.DeviceId, DeviceName = s?.DeviceName, Client = s?.Client, ApplicationVersion = s?.ApplicationVersion,
            RemoteEndPoint = s?.RemoteEndPoint, Success = true,
        };
        _queue.Enqueue(() => _store.UpsertLogins(new[] { rec }));
        return Task.CompletedTask;
    }
}

public sealed class LoginFailedConsumer : IEventConsumer<AuthenticationRequestEventArgs>
{
    private readonly AnnexStore _store; private readonly WriteQueue _queue; private readonly IClock _clock;
    public LoginFailedConsumer(AnnexStore store, WriteQueue queue, IClock clock) { _store = store; _queue = queue; _clock = clock; }

    public Task OnEvent(AuthenticationRequestEventArgs e)
    {
        var at = _clock.UtcNow;
        var rec = new LoginRecord
        {
            Id = $"login-{at.UtcTicks}-{e.DeviceId}", At = at, UserId = e.UserId?.ToString("N"), UserName = e.Username,
            DeviceId = e.DeviceId, DeviceName = e.DeviceName, Client = e.App, ApplicationVersion = e.AppVersion,
            RemoteEndPoint = e.RemoteEndPoint, Success = false,
        };
        _queue.Enqueue(() => _store.UpsertLogins(new[] { rec }));
        return Task.CompletedTask;
    }
}
