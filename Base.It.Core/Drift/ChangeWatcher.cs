using System.Threading.Channels;
using Base.It.Core.Logging;
using Base.It.Core.Models;

namespace Base.It.Core.Drift;

/// <summary>
/// Supplies the parameters for a single watcher tick. Called on every
/// interval — implementations may return a fresh object list each time
/// (e.g. reloaded from the Batch xlsx, or auto-discovered from
/// <c>sys.objects</c>) or a cached list. Return an empty <see cref="Ids"/>
/// collection to skip the tick without logging an error.
/// </summary>
public sealed record WatchPlan(
    string                                SourceConn,
    string                                TargetConn,
    string                                SourceEnv,
    string                                TargetEnv,
    IReadOnlyCollection<ObjectIdentifier> Ids);

/// <summary>
/// Non-blocking periodic drift poller. Owns one background <see cref="Task"/>
/// that runs the <see cref="DriftDetector"/> on a fixed interval, streaming
/// each object's result as it resolves via an event channel:
/// <see cref="TickStarted"/>, N × <see cref="ObjectDrifted"/>, then
/// <see cref="TickCompleted"/>. UI consumers listen on <see cref="Events"/>
/// and upsert rows live; if they fall behind, the oldest event is dropped
/// so memory stays bounded and the poll loop never blocks.
///
/// Lifecycle: <see cref="Start"/> once; call <see cref="StopAsync"/> (or
/// <see cref="DisposeAsync"/>) to shut down cleanly.
/// </summary>
public sealed class ChangeWatcher : IAsyncDisposable
{
    private readonly DriftDetector _detector;
    private readonly FileLogger _logger;
    private readonly TimeSpan _interval;
    private readonly Func<CancellationToken, Task<WatchPlan?>> _planSupplier;
    private readonly Channel<WatchEvent> _channel;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _running; // 0 = stopped, 1 = running; Interlocked-guarded

    public ChangeWatcher(
        DriftDetector detector,
        FileLogger logger,
        TimeSpan interval,
        Func<CancellationToken, Task<WatchPlan?>> planSupplier,
        int channelCapacity = 256)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        if (channelCapacity < 1) throw new ArgumentOutOfRangeException(nameof(channelCapacity));

        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
        _interval = interval;
        _planSupplier = planSupplier ?? throw new ArgumentNullException(nameof(planSupplier));

        // DropOldest: a slow consumer must never stall the poll loop. A
        // burst of per-object events in a single tick can fill a tighter
        // channel quickly, so we default to 256 — still bounded, still
        // impossible to stall the poller.
        _channel = Channel.CreateBounded<WatchEvent>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true
        });
    }

    /// <summary>Reader for the live event stream. Safe to enumerate from any thread.</summary>
    public ChannelReader<WatchEvent> Events => _channel.Reader;

    /// <summary>Starts the polling loop. Idempotent — subsequent calls are no-ops.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _cts  = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Stops the polling loop and awaits the background task.</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0) return;
        _cts?.Cancel();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
        finally
        {
            _cts?.Dispose();
            _cts  = null;
            _loop = null;
            _channel.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var plan = await _planSupplier(ct).ConfigureAwait(false);
                if (plan is not null && plan.Ids.Count > 0)
                {
                    var at = DateTime.UtcNow;
                    await _channel.Writer.WriteAsync(
                        new TickStarted(plan.SourceEnv, plan.TargetEnv, at, plan.Ids.Count),
                        ct).ConfigureAwait(false);

                    // Stream per-object drifts as they complete. Also
                    // accumulate a DriftBatch for the TickCompleted event so
                    // consumers that want the legacy shape still have it.
                    var accumulated = new List<ObjectDrift>(plan.Ids.Count);
                    int changed = 0, errors = 0;
                    await foreach (var d in _detector.StreamAsync(
                                       plan.SourceConn, plan.TargetConn, plan.Ids, ct)
                                   .ConfigureAwait(false))
                    {
                        accumulated.Add(d);
                        if (d.IsSyncable)             changed++;
                        if (d.Kind == DriftKind.Error) errors++;
                        await _channel.Writer.WriteAsync(
                            new ObjectDrifted(plan.SourceEnv, plan.TargetEnv, d),
                            ct).ConfigureAwait(false);
                    }

                    var batch = new DriftBatch(plan.SourceEnv, plan.TargetEnv, at, accumulated);
                    await _channel.Writer.WriteAsync(
                        new TickCompleted(plan.SourceEnv, plan.TargetEnv, at,
                            accumulated.Count, changed, errors, batch),
                        ct).ConfigureAwait(false);

                    if (changed > 0 || errors > 0)
                        _logger.Log($"Watcher {plan.SourceEnv}->{plan.TargetEnv}: {changed} changed, {errors} errors of {accumulated.Count}.");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.Log($"Watcher tick failed: {ex.Message}");
            }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
