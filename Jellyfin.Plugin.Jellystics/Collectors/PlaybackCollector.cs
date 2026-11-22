using Jellyfin.Plugin.Jellystics.Models;
using Jellyfin.Plugin.Jellystics.Storage;
using Jellyfin.Plugin.Jellystics.Tracking;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellystics.Collectors;

/// <summary>Session events → registry; checkpoints every 15 s; sweeps silent plays every 30 s; closes stale open plays at startup.</summary>
public sealed class PlaybackCollector : IHostedService, IDisposable
{
    public static readonly TimeSpan CheckpointEvery = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(30);

    private readonly ISessionManager _sessions;
    private readonly PlaySessionRegistry _registry;
    private readonly PlayRecordFactory _factory;
    private readonly PlayStore _store;
    private readonly WriteQueue _queue;
    private readonly IClock _clock;
    private readonly ILogger<PlaybackCollector> _logger;
    private Timer? _checkpoint;
    private Timer? _sweep;

    public PlaybackCollector(ISessionManager sessions, PlaySessionRegistry registry, PlayRecordFactory factory, PlayStore store, WriteQueue queue, IClock clock, ILogger<PlaybackCollector> logger)
    {
        _sessions = sessions; _registry = registry; _factory = factory; _store = store; _queue = queue; _clock = clock; _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _queue.Enqueue(() => { var n = _store.CloseOpenPlays(); if (n > 0) _logger.LogInformation("Jellystics closed {Count} plays left open", n); });
        _sessions.PlaybackStart += OnStart;
        _sessions.PlaybackProgress += OnProgress;
        _sessions.PlaybackStopped += OnStopped;
        _checkpoint = new Timer(_ => Persist(_registry.TakeDirtyOpen()), null, CheckpointEvery, CheckpointEvery);
        _sweep = new Timer(_ => Persist(_registry.Sweep()), null, SweepEvery, SweepEvery);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnStart;
        _sessions.PlaybackProgress -= OnProgress;
        _sessions.PlaybackStopped -= OnStopped;
        _checkpoint?.Dispose(); _sweep?.Dispose();
        Persist(_registry.TakeDirtyOpen());
        return Task.CompletedTask;
    }

    public void Dispose() { _checkpoint?.Dispose(); _sweep?.Dispose(); }

    private void OnStart(object? sender, PlaybackProgressEventArgs e) => Handle(e, PlayEventKind.Start, null);
    private void OnProgress(object? sender, PlaybackProgressEventArgs e) => Handle(e, PlayEventKind.Progress, null);
    private void OnStopped(object? sender, PlaybackStopEventArgs e) => Handle(e, PlayEventKind.Stop, e.PlayedToCompletion);

    private void Handle(PlaybackProgressEventArgs e, PlayEventKind kind, bool? done)
    {
        try
        {
            if (e.Item is null || e.Users.Count == 0 || e.Item.IsThemeMedia) return;
            var key = PlayRecordFactory.Key(e);
            // Library / media-stream lookups happen here, outside the registry lock.
            PlayRecord? record = kind == PlayEventKind.Stop ? null : (kind == PlayEventKind.Start || !_registry.IsOpen(key) ? _factory.Create(e) : null);
            var ev = _factory.ToEvent(e, kind, default, done);   // At is stamped by the registry, inside its lock
            var closed = _registry.OnEventNow(key, PlayRecordFactory.DeviceUserKey(e), () => record ??= _factory.Create(e), ev);
            if (kind == PlayEventKind.Progress) _registry.Mutate(key, r => _factory.ApplyTranscoding(r, e.Session));
            Persist(closed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jellystics failed to handle a {Kind} event", kind);
        }
    }

    private void Persist(IReadOnlyList<PlayRecord> plays)
    {
        if (plays.Count == 0) return;
        _queue.Enqueue(() => _store.Upsert(plays));
    }
}
