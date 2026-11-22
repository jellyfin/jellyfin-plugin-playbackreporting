namespace Jellyfin.Plugin.Jellystics.Import;

public sealed record ImportStatus(string State, int Read, int Imported, int Skipped, int Repaired, string? Error, DateTimeOffset? FinishedAt);

/// <summary>Runs at most one Playback Reporting import at a time on a background task.</summary>
public sealed class ImportJobs
{
    private readonly Func<string, IProgress<int>, PbrImportResult> _runner;
    private readonly object _gate = new();
    private ImportStatus _status = new("idle", 0, 0, 0, 0, null, null);

    public ImportJobs(Func<string, IProgress<int>, PbrImportResult> runner) => _runner = runner;

    public ImportStatus Status { get { lock (_gate) return _status; } }

    public bool TryStartPbr(string path)
    {
        lock (_gate)
        {
            if (_status.State == "running") return false;
            _status = new ImportStatus("running", 0, 0, 0, 0, null, null);
        }

        _ = Task.Run(() =>
        {
            ImportStatus final;
            try
            {
                var r = _runner(path, new SyncProgress(read => { lock (_gate) _status = _status with { Read = read }; }));
                final = new ImportStatus("done", r.Read, r.Imported, r.Skipped, r.Repaired, null, DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                lock (_gate) final = _status with { State = "failed", Error = ex.Message, FinishedAt = DateTimeOffset.UtcNow };   // keeps the rows read so far
            }

            lock (_gate) _status = final;
        });
        return true;
    }

    /// <summary>Synchronous IProgress (Progress&lt;T&gt; would post to the thread pool and reorder updates).</summary>
    private sealed class SyncProgress : IProgress<int>
    {
        private readonly Action<int> _on;
        public SyncProgress(Action<int> on) => _on = on;
        public void Report(int value) => _on(value);
    }
}
