using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Base.It.Core.Abstractions;
using Base.It.Core.Models;

namespace Base.It.Core.Drift;

/// <summary>
/// Per-request change detection: given a set of object identifiers, fetches
/// each from source and target in parallel (bounded by a semaphore) and
/// classifies the outcome as <see cref="DriftKind"/>. Pure, cancellable,
/// non-blocking for UI threads.
/// </summary>
public sealed class DriftDetector
{
    private readonly IObjectScripter _scripter;
    private readonly int _maxParallelism;

    /// <param name="maxParallelism">
    /// Cap on simultaneous SQL fetches. Default 4 is a comfortable ceiling
    /// for a SQL Server on a WAN link; raise it for LAN, lower it if the
    /// shared pool is under pressure.
    /// </param>
    public DriftDetector(IObjectScripter scripter, int maxParallelism = 4)
    {
        if (maxParallelism < 1) throw new ArgumentOutOfRangeException(nameof(maxParallelism));
        _scripter = scripter ?? throw new ArgumentNullException(nameof(scripter));
        _maxParallelism = maxParallelism;
    }

    public async Task<DriftBatch> CompareAsync(
        string sourceConn, string targetConn,
        string sourceEnv, string targetEnv,
        IReadOnlyCollection<ObjectIdentifier> ids,
        CancellationToken ct = default)
    {
        if (ids is null || ids.Count == 0)
            return new DriftBatch(sourceEnv, targetEnv, DateTime.UtcNow, Array.Empty<ObjectDrift>());

        using var gate = new SemaphoreSlim(_maxParallelism);
        var tasks = ids.Select(id => CompareOneAsync(sourceConn, targetConn, id, gate, ct)).ToArray();
        var results = await Task.WhenAll(tasks);
        return new DriftBatch(sourceEnv, targetEnv, DateTime.UtcNow, results);
    }

    /// <summary>
    /// Yields each <see cref="ObjectDrift"/> as soon as its own source+target
    /// fetches resolve — the caller can render partial progress instead of
    /// waiting for the full set. Parallelism and error-isolation semantics
    /// are identical to <see cref="CompareAsync"/>.
    /// </summary>
    public async IAsyncEnumerable<ObjectDrift> StreamAsync(
        string sourceConn, string targetConn,
        IReadOnlyCollection<ObjectIdentifier> ids,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (ids is null || ids.Count == 0) yield break;

        // Internal CTS linked to the external token. If the enumerator is
        // abandoned mid-stream, the finally below cancels this so every
        // in-flight SQL command unwinds — we never leak a producer.
        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var innerCt = producerCts.Token;

        // Unbounded channel: producers never block, consumer reads in
        // completion order (not submission order).
        var chan = Channel.CreateUnbounded<ObjectDrift>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        // Producer owns the semaphore so there's no disposal race with
        // the enumerator's teardown.
        var producer = Task.Run(async () =>
        {
            using var gate = new SemaphoreSlim(_maxParallelism);
            try
            {
                var tasks = ids.Select(async id =>
                {
                    var drift = await CompareOneAsync(sourceConn, targetConn, id, gate, innerCt).ConfigureAwait(false);
                    chan.Writer.TryWrite(drift);
                }).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* expected when consumer bails */ }
            finally { chan.Writer.TryComplete(); }
        });

        try
        {
            await foreach (var item in chan.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            // If the consumer broke out early (exception, enumerator
            // dispose, or cancellation), signal the producer to abort
            // and await it so no SQL commands outlive this call.
            producerCts.Cancel();
            try { await producer.ConfigureAwait(false); } catch { /* swallowed */ }
        }
    }

    private async Task<ObjectDrift> CompareOneAsync(
        string sourceConn, string targetConn,
        ObjectIdentifier id, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            // Parallelize the two fetches within a single pair — they hit
            // different servers so there's no contention, and halves the
            // per-object wall-clock when latency dominates.
            var srcTask = _scripter.GetObjectAsync(sourceConn, id, ct);
            var tgtTask = _scripter.GetObjectAsync(targetConn, id, ct);
            await Task.WhenAll(srcTask, tgtTask);

            var src = srcTask.Result;
            var tgt = tgtTask.Result;

            return (src, tgt) switch
            {
                (null, null) => new ObjectDrift(id, DriftKind.MissingInSource, SqlObjectType.Unknown, SqlObjectType.Unknown, null, null,
                    "Object not found in either environment."),
                (null, not null) => new ObjectDrift(id, DriftKind.MissingInSource, SqlObjectType.Unknown, tgt.Type, null, tgt.Hash),
                (not null, null) => new ObjectDrift(id, DriftKind.MissingInTarget, src.Type, SqlObjectType.Unknown, src.Hash, null),
                _ when string.Equals(src!.Hash, tgt!.Hash, StringComparison.OrdinalIgnoreCase)
                    => new ObjectDrift(id, DriftKind.InSync, src.Type, tgt.Type, src.Hash, tgt.Hash),
                _ => new ObjectDrift(id, DriftKind.Different, src!.Type, tgt!.Type, src.Hash, tgt.Hash)
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ObjectDrift(id, DriftKind.Error, SqlObjectType.Unknown, SqlObjectType.Unknown, null, null, ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }
}
