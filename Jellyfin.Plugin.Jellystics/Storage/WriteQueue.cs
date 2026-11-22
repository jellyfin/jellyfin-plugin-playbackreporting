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

    public void Enqueue(Action work) => _channel.Writer.TryWrite(work);

    public Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() => done.SetResult());
        return done.Task;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var work in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try { work(); }
                catch (Exception ex) { _logger.LogError(ex, "Jellystics write failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        while (_channel.Reader.TryRead(out var work)) { try { work(); } catch (Exception ex) { _logger.LogError(ex, "Jellystics write failed"); } }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
