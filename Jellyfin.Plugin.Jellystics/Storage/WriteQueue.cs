using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellystics.Storage;

/// <summary>Single-writer queue: every SQLite write runs here, sequentially, off Jellyfin's event threads.</summary>
public sealed class WriteQueue : BackgroundService
{
    private readonly Channel<Action> _channel = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ILogger<WriteQueue> _logger;

    public WriteQueue(ILogger<WriteQueue> logger) => _logger = logger;

    /// <summary>Queues work; returns false (and drops it) once the queue has been stopped.</summary>
    public bool Enqueue(Action work)
    {
        if (_channel.Writer.TryWrite(work)) return true;
        _logger.LogWarning("Jellystics write queue is stopped, work dropped");
        return false;
    }

    /// <summary>Runs <paramref name="work"/> on the queue and returns its result; faults with the work's exception, or fails immediately when the queue is stopped.</summary>
    public Task<T> RunAsync<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = Enqueue(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        if (!queued) done.SetException(new InvalidOperationException("The write queue is stopped"));
        return done.Task;
    }

    /// <summary>Completes when everything queued before this call has run (immediately when the queue is stopped).</summary>
    public Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return Enqueue(() => done.SetResult()) ? done.Task : Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not tied to the stopping token: the loop ends only when the writer is completed, after the backlog has been drained.
        await foreach (var work in _channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false)) Run(work);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        if (ExecuteTask is { } running)
        {
            try { await running.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { _logger.LogWarning("Jellystics write queue did not drain before shutdown"); }
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Run(Action work)
    {
        try { work(); return; }
        catch (Exception first) { _logger.LogWarning(first, "Jellystics write failed, retrying once"); }
        try { work(); }
        catch (Exception ex) { _logger.LogError(ex, "Jellystics write failed"); }
    }
}
