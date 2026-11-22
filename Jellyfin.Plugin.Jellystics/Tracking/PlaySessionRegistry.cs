using Jellyfin.Plugin.Jellystics.Models;

namespace Jellyfin.Plugin.Jellystics.Tracking;

/// <summary>Open plays by key; thread-safe (all public methods lock).</summary>
public sealed class PlaySessionRegistry
{
    public static readonly TimeSpan SilentPlayingAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SilentPausedAfter = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan ClosedKeyMemory = TimeSpan.FromMinutes(2);

    private readonly IClock _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, (PlayTracker Tracker, string DeviceUser)> _open = new();
    private readonly Dictionary<string, DateTimeOffset> _recentlyClosed = new();

    public PlaySessionRegistry(IClock clock) => _clock = clock;

    public int OpenCount { get { lock (_lock) { return _open.Count; } } }

    public bool IsOpen(string key) { lock (_lock) { return _open.ContainsKey(key); } }

    /// <summary>
    /// Like <see cref="OnEvent"/> but stamps the event with the registry clock inside the lock,
    /// so concurrent events can never be applied out of order. <paramref name="newRecord"/> should
    /// return a pre-built record (no library lookups under the lock).
    /// </summary>
    public IReadOnlyList<PlayRecord> OnEventNow(string key, string deviceUserKey, Func<PlayRecord> newRecord, PlayEvent e)
    {
        lock (_lock) { return Handle(key, deviceUserKey, newRecord, e with { At = _clock.UtcNow }); }
    }

    public IReadOnlyList<PlayRecord> OnEvent(string key, string deviceUserKey, Func<PlayRecord> newRecord, PlayEvent e)
    {
        lock (_lock) { return Handle(key, deviceUserKey, newRecord, e); }
    }

    private IReadOnlyList<PlayRecord> Handle(string key, string deviceUserKey, Func<PlayRecord> newRecord, PlayEvent e)
    {
        // A stop for a play we do not know must never touch other plays.
        if (e.Kind == PlayEventKind.Stop && !_open.ContainsKey(key)) return Array.Empty<PlayRecord>();
        // Late events for a play that was just closed must not resurrect it.
        if (e.Kind != PlayEventKind.Start && _recentlyClosed.ContainsKey(key)) return Array.Empty<PlayRecord>();

        var closed = new List<PlayRecord>();
        PlayRecord? prebuilt = null;
        if (e.Kind == PlayEventKind.Start && _open.TryGetValue(key, out var existing))
        {
            prebuilt = newRecord();
            // Duplicate Start for the same item: just a progress, do not split the play.
            if (prebuilt.ItemId == existing.Tracker.Record.ItemId) { e = e with { Kind = PlayEventKind.Progress }; prebuilt = null; }
        }

        if (e.Kind == PlayEventKind.Start || !_open.ContainsKey(key))
        {
            foreach (var (k, v) in _open.Where(kv => kv.Value.DeviceUser == deviceUserKey || kv.Key == key).ToList())
            {
                v.Tracker.CloseAt(e.At);
                closed.Add(v.Tracker.Snapshot());
                _open.Remove(k);
                _recentlyClosed[k] = _clock.UtcNow;
            }
            _recentlyClosed.Remove(key);
            _open[key] = (new PlayTracker(prebuilt ?? newRecord(), e with { Kind = PlayEventKind.Start }), deviceUserKey);
            return closed;
        }

        var tracker = _open[key].Tracker;
        tracker.Apply(e);
        if (tracker.Record.State == PlayStates.Closed)
        {
            closed.Add(tracker.Snapshot());
            _open.Remove(key);
            _recentlyClosed[key] = _clock.UtcNow;
        }
        return closed;
    }

    public IReadOnlyList<PlayRecord> Sweep()
    {
        lock (_lock)
        {
            var now = _clock.UtcNow;
            foreach (var (k, at) in _recentlyClosed.ToList()) if (now - at > ClosedKeyMemory) _recentlyClosed.Remove(k);
            var closed = new List<PlayRecord>();
            foreach (var (k, v) in _open.ToList())
            {
                var silent = now - v.Tracker.Record.LastEventAt;
                if (silent > (v.Tracker.IsPlaying ? SilentPlayingAfter : SilentPausedAfter))
                {
                    v.Tracker.CloseAt(v.Tracker.Record.LastEventAt);
                    closed.Add(v.Tracker.Snapshot());
                    _open.Remove(k);
                    _recentlyClosed[k] = now;
                }
            }
            return closed;
        }
    }

    /// <summary>Applies an in-place change to an open play and marks it dirty. No-op for unknown keys.</summary>
    public void Mutate(string key, Action<PlayRecord> change)
    {
        lock (_lock)
        {
            if (!_open.TryGetValue(key, out var v)) return;
            change(v.Tracker.Record);
            v.Tracker.Touch();
        }
    }

    public IReadOnlyList<PlayRecord> TakeDirtyOpen()
    {
        lock (_lock) { return _open.Values.Where(v => v.Tracker.Dirty).Select(v => v.Tracker.Snapshot()).ToList(); }
    }
}
