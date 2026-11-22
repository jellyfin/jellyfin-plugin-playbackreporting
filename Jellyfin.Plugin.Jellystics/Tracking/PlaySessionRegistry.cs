using Jellyfin.Plugin.Jellystics.Models;

namespace Jellyfin.Plugin.Jellystics.Tracking;

/// <summary>Open plays by key; thread-safe (all public methods lock).</summary>
public sealed class PlaySessionRegistry
{
    public static readonly TimeSpan SilentPlayingAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SilentPausedAfter = TimeSpan.FromMinutes(60);

    private readonly IClock _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, (PlayTracker Tracker, string DeviceUser)> _open = new();

    public PlaySessionRegistry(IClock clock) => _clock = clock;

    public int OpenCount { get { lock (_lock) { return _open.Count; } } }

    public IReadOnlyList<PlayRecord> OnEvent(string key, string deviceUserKey, Func<PlayRecord> newRecord, PlayEvent e)
    {
        lock (_lock)
        {
            var closed = new List<PlayRecord>();
            if (e.Kind == PlayEventKind.Start || !_open.ContainsKey(key))
            {
                foreach (var (k, v) in _open.Where(kv => kv.Value.DeviceUser == deviceUserKey || kv.Key == key).ToList())
                {
                    v.Tracker.CloseAt(e.At);
                    closed.Add(v.Tracker.Snapshot());
                    _open.Remove(k);
                }
                if (e.Kind == PlayEventKind.Stop) return closed;   // stop for a play we never saw: nothing to record
                var startEvent = e with { Kind = PlayEventKind.Start };
                _open[key] = (new PlayTracker(newRecord(), startEvent), deviceUserKey);
                return closed;
            }

            var tracker = _open[key].Tracker;
            tracker.Apply(e);
            if (tracker.Record.State == PlayStates.Closed)
            {
                closed.Add(tracker.Snapshot());
                _open.Remove(key);
            }
            return closed;
        }
    }

    public IReadOnlyList<PlayRecord> Sweep()
    {
        lock (_lock)
        {
            var now = _clock.UtcNow;
            var closed = new List<PlayRecord>();
            foreach (var (k, v) in _open.ToList())
            {
                var silent = now - v.Tracker.Record.LastEventAt;
                if (silent > (v.Tracker.IsPlaying ? SilentPlayingAfter : SilentPausedAfter))
                {
                    v.Tracker.CloseAt(v.Tracker.Record.LastEventAt);
                    closed.Add(v.Tracker.Snapshot());
                    _open.Remove(k);
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
