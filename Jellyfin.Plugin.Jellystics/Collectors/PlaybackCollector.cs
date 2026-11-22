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
        _checkpoint = new Timer(_ => Guarded("checkpoint", () => Persist(_registry.TakeDirtyOpen())), null, CheckpointEvery, CheckpointEvery);
        _sweep = new Timer(_ => Guarded("sweep", () => { Persist(_registry.Sweep()); _factory.EvictIdle(TimeSpan.FromMinutes(10)); }), null, SweepEvery, SweepEvery);
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

    /// <summary>
    /// Which events become plays. Deliberate deviation from spec 4.2 (which accepted them): automated progress events are dropped,
    /// they are server generated and would credit time that was not watched. Extras (trailers, theme media) are never plays.
    /// </summary>
    internal static bool ShouldTrack(PlaybackProgressEventArgs e) =>
        e.Item is not null && e.Users.Count > 0 && !e.IsAutomated && e.Item.ExtraType is null && !e.Item.IsThemeMedia;

    private void Guarded(string what, Action a)
    {
        try { a(); } catch (Exception ex) { _logger.LogError(ex, "Jellystics {What} failed", what); }
    }

    // Start and Stop are raised from thread-pool tasks by SessionManager while Progress is synchronous: ordering between them is not
    // guaranteed, hence the registry stamps events under its lock and ignores late events for plays it just closed.
    private void OnStart(object? sender, PlaybackProgressEventArgs e) => Handle(e, PlayEventKind.Start, null);
    private void OnProgress(object? sender, PlaybackProgressEventArgs e) => Handle(e, PlayEventKind.Progress, null);
    private void OnStopped(object? sender, PlaybackStopEventArgs e) => Handle(e, PlayEventKind.Stop, e.PlayedToCompletion);

    private void Handle(PlaybackProgressEventArgs e, PlayEventKind kind, bool? done)
    {
        try
        {
            if (!ShouldTrack(e)) return;
            var deviceUser = PlayRecordFactory.DeviceUserKey(e);
            // Some Stop events lack the PlaySessionId: fall back to the open play of the same device, user and item.
            var key = kind == PlayEventKind.Start ? PlayRecordFactory.Key(e) : _registry.ResolveKey(PlayRecordFactory.Key(e), deviceUser, e.Item!.Id.ToString("N"));
            // Library / media-stream lookups happen here, outside the registry lock.
            PlayRecord? record = kind == PlayEventKind.Stop ? null : (kind == PlayEventKind.Start || !_registry.IsOpen(key) ? _factory.Create(e) : null);
            var ev = _factory.ToEvent(e, kind, default, done);   // At is stamped by the registry, inside its lock
            var closed = _registry.OnEventNow(key, deviceUser, () => record ??= _factory.Create(e), ev);
            if (kind == PlayEventKind.Progress)
            {
                var update = _factory.PrepareTranscodeUpdate(e);   // stream lookups happen here, outside the registry lock
                _registry.Mutate(key, update);
            }
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
        foreach (var p in plays) _factory.FinalizeClosed(p);
        _queue.Enqueue(() => _store.Upsert(plays));
    }
}
