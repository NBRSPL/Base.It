using System.Diagnostics;

namespace Base.It.Core.Schema;

/// <summary>
/// Produces a <see cref="Snapshot"/> from a live SQL Server. Two paths:
/// <list type="bullet">
///   <item><b>Full</b> — first snapshot, or when the previous snapshot
///         doesn't carry per-object <c>modify_date</c>. Streams every
///         module + every table's columns from <see cref="BulkSchemaFetcher.FetchAllAsync"/>.</item>
///   <item><b>Incremental</b> — every subsequent snapshot. Pulls a
///         lightweight metadata-only list (no definitions) via
///         <see cref="BulkSchemaFetcher.FetchMetadataAsync"/>, compares
///         each object's <c>modify_date</c> against the previous
///         snapshot, and only fetches definitions for the ones that
///         actually changed. Unchanged objects reuse their hash from
///         the previous snapshot — zero bytes over the wire for them.</item>
/// </list>
///
/// Steady-state cost on an unchanged 7,000-object database: one tiny
/// catalog query + one snapshot pointer file. Should finish in 1-3
/// seconds regardless of network.
/// </summary>
public sealed class SchemaSnapshotter
{
    private readonly SchemaStore _store;
    private readonly BulkSchemaFetcher _fetcher;

    /// <summary>How many object files to write concurrently. Disk I/O is mostly wait.</summary>
    private const int MaxParallelWrites = 32;

    public SchemaSnapshotter(SchemaStore store)
    {
        _store   = store ?? throw new ArgumentNullException(nameof(store));
        _fetcher = new BulkSchemaFetcher();
    }

    public async Task<SnapshotResult> SnapshotAsync(
        string connectionString,
        string environment,
        string database,
        IProgress<SnapshotProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("connectionString required", nameof(connectionString));

        var totalSw = Stopwatch.StartNew();

        // ─── Load the previous snapshot (if any) for incremental diffing ───
        Snapshot? previous = await LoadIncrementalBaselineAsync(ct);
        var prevByKey = previous?.Entries
            .Where(e => e.ModifiedAtUtc.HasValue)
            .ToDictionary(e => e.Key, e => e)
            ?? new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);

        // ─── Phase 1: fetch ───
        progress?.Report(new SnapshotProgress(SnapshotPhase.Fetching, 0, 0, TimeSpan.Zero));
        var fetchSw = Stopwatch.StartNew();
        var fetchProgress = new Progress<int>(rowsSoFar =>
            progress?.Report(new SnapshotProgress(SnapshotPhase.Fetching, rowsSoFar, 0, fetchSw.Elapsed)));

        IReadOnlyList<Base.It.Core.Models.SqlObject> fetched;
        List<SnapshotEntry> reused;
        bool usedCompression;
        int reusedCount;
        int fetchedCount;
        bool wasIncremental;
        int connectionsUsed = 0;

        if (prevByKey.Count > 0)
        {
            wasIncremental = true;

            // Lightweight metadata: one tiny query, no definitions.
            var metadata = await _fetcher.FetchMetadataAsync(connectionString, ct);

            // Pair every current object with its previous-snapshot entry
            // (if any). Unchanged modify_date AND same Kind = safe to
            // reuse the hash. Otherwise, fetch a fresh definition.
            var idsToFetch = new List<int>();
            reused = new List<SnapshotEntry>(metadata.Count);
            foreach (var m in metadata)
            {
                var key = $"{m.Schema.ToUpperInvariant()}.{m.Name.ToUpperInvariant()}";
                if (prevByKey.TryGetValue(key, out var prev)
                    && prev.Kind == m.Kind
                    && prev.ModifiedAtUtc.HasValue
                    && prev.ModifiedAtUtc.Value == m.ModifyDateUtc)
                {
                    // Reuse: same name, same kind, same modify_date.
                    // Refresh the modify_date in case it was stored
                    // with an off-by-tick precision.
                    reused.Add(prev with { ModifiedAtUtc = m.ModifyDateUtc });
                }
                else
                {
                    idsToFetch.Add(m.ObjectId);
                }
            }

            reusedCount  = reused.Count;
            fetchedCount = idsToFetch.Count;

            // Surface the reuse split so the UI's progress text can show
            // "reused N, fetching M" while the by-id query runs.
            progress?.Report(new SnapshotProgress(
                SnapshotPhase.Fetching, 0, fetchedCount, fetchSw.Elapsed,
                ReusedFromPrevious: reusedCount));

            if (idsToFetch.Count > 0)
            {
                var res = await _fetcher.FetchByObjectIdsAsync(connectionString, idsToFetch, fetchProgress, ct);
                fetched = res.Objects;
                usedCompression = res.UsedCompression;
                connectionsUsed = res.Connections;
            }
            else
            {
                fetched = Array.Empty<Base.It.Core.Models.SqlObject>();
                usedCompression = false;
                connectionsUsed = 0;
            }

            // Build modify_date lookup so the fresh fetches inherit the
            // current server's modify_date (so the next snapshot can
            // reuse them in turn).
            var metaByKey = metadata.ToDictionary(
                m => $"{m.Schema.ToUpperInvariant()}.{m.Name.ToUpperInvariant()}",
                m => m,
                StringComparer.OrdinalIgnoreCase);
            // Stitched entries computed below in Phase 3.
        }
        else
        {
            wasIncremental = false;
            var res = await _fetcher.FetchAllAsync(connectionString, fetchProgress, ct);
            fetched         = res.Objects;
            usedCompression = res.UsedCompression;
            connectionsUsed = res.Connections;
            reused          = new List<SnapshotEntry>(0);
            reusedCount     = 0;
            fetchedCount    = fetched.Count;
        }

        fetchSw.Stop();

        var total = reused.Count + fetched.Count;
        progress?.Report(new SnapshotProgress(
            SnapshotPhase.Writing, reused.Count, total, fetchSw.Elapsed,
            ReusedFromPrevious: reusedCount));

        // ─── Phase 2: write fresh objects to the store, in parallel ───
        // Reused entries are already on disk (their hash was in the
        // previous snapshot, so objects/{hash}.sql.gz already exists).
        // We only need to write the freshly-fetched ones.
        var freshEntries = new SnapshotEntry[fetched.Count];
        int written = reused.Count;
        var writeSw = Stopwatch.StartNew();

        // Build a modify_date lookup for fresh fetches so each freshly-
        // hashed entry remembers its server-side modify_date.
        var fetchedMetaByKey = wasIncremental
            ? (await _fetcher.FetchMetadataAsync(connectionString, ct))  // re-fetch is cheap and reliable
                .ToDictionary(
                    m => $"{m.Schema.ToUpperInvariant()}.{m.Name.ToUpperInvariant()}",
                    m => m.ModifyDateUtc,
                    StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        using var gate = new SemaphoreSlim(MaxParallelWrites);
        var tasks = new List<Task>(fetched.Count);
        for (int i = 0; i < fetched.Count; i++)
        {
            var idx = i;
            tasks.Add(WriteOneAsync(idx));
        }
        await Task.WhenAll(tasks);
        writeSw.Stop();

        async Task WriteOneAsync(int idx)
        {
            await gate.WaitAsync(ct);
            try
            {
                var obj = fetched[idx];
                await _store.WriteObjectAsync(obj.Hash, obj.Definition, ct);

                DateTime? modifyAt = null;
                if (fetchedMetaByKey.TryGetValue(
                    $"{obj.Id.Schema.ToUpperInvariant()}.{obj.Id.Name.ToUpperInvariant()}",
                    out var m))
                {
                    modifyAt = m;
                }

                freshEntries[idx] = new SnapshotEntry(
                    Schema:        obj.Id.Schema,
                    Name:          obj.Id.Name,
                    Kind:          obj.Type,
                    Hash:          obj.Hash,
                    Size:          obj.Definition.Length,
                    ModifiedAtUtc: modifyAt);

                var n = Interlocked.Increment(ref written);
                if (n == total || n == reused.Count + 1 || (n - reused.Count) % 25 == 0)
                    progress?.Report(new SnapshotProgress(
                        SnapshotPhase.Writing, n, total, fetchSw.Elapsed,
                        ReusedFromPrevious: reusedCount));
            }
            finally { gate.Release(); }
        }

        // ─── Phase 3: combine reused + freshly fetched → snapshot file ───
        var pointerSw = Stopwatch.StartNew();
        var allEntries = new List<SnapshotEntry>(reused.Count + freshEntries.Length);
        allEntries.AddRange(reused);
        foreach (var e in freshEntries) if (e is not null) allEntries.Add(e);

        // Backfill trigger → parent-table info on every entry. Cheap
        // (one catalog query) and applied to both reused entries (which
        // may have come from a legacy snapshot without these fields) and
        // freshly fetched entries (the bulk fetcher's modules path
        // doesn't populate them inline). Entries that already have a
        // parent set are left alone — a noisy fallback in case the
        // database state diverged mid-snapshot.
        var triggerParents = await _fetcher.FetchTriggerParentsAsync(connectionString, ct);
        if (triggerParents.Count > 0)
        {
            for (int i = 0; i < allEntries.Count; i++)
            {
                var e = allEntries[i];
                if (e.Kind != Base.It.Core.Models.SqlObjectType.Trigger) continue;
                if (e.HasParent) continue;
                if (triggerParents.TryGetValue(e.Key, out var p))
                {
                    allEntries[i] = e with { ParentSchema = p.Schema, ParentName = p.Name };
                }
            }
        }

        var snapshot = new Snapshot(
            Id:          SchemaStore.NewSnapshotId(),
            TakenAtUtc:  DateTime.UtcNow,
            Environment: environment,
            Database:    database,
            Entries:     allEntries);

        await _store.WriteSnapshotAsync(snapshot, ct);
        pointerSw.Stop();
        totalSw.Stop();

        var timing = new SnapshotTiming(
            Fetch:           fetchSw.Elapsed,
            Write:           writeSw.Elapsed,
            Pointer:         pointerSw.Elapsed,
            Total:           totalSw.Elapsed,
            UsedCompression: usedCompression,
            WasIncremental:  wasIncremental,
            ReusedCount:     reusedCount,
            FetchedCount:    fetchedCount,
            Connections:     connectionsUsed);

        progress?.Report(new SnapshotProgress(
            SnapshotPhase.Done, total, total, fetchSw.Elapsed,
            ReusedFromPrevious: reusedCount));

        return new SnapshotResult(snapshot, timing);
    }

    /// <summary>
    /// Pull the most-recent snapshot from the store if any exists, else
    /// null. Used as the baseline for the modify_date diff.
    /// </summary>
    private async Task<Snapshot?> LoadIncrementalBaselineAsync(CancellationToken ct)
    {
        var summaries = _store.ListSnapshots();
        if (summaries.Count == 0) return null;
        return await _store.ReadSnapshotAsync(summaries[0].Id, ct);
    }
}

/// <summary>What stage the snapshotter is currently in.</summary>
public enum SnapshotPhase { Fetching, Writing, Done }

/// <summary>Per-call progress signal.</summary>
public sealed record SnapshotProgress(
    SnapshotPhase Phase,
    int Done,
    int Total,
    TimeSpan FetchTime,
    int ReusedFromPrevious = 0);

/// <summary>Wall-clock + provenance breakdown of a snapshot run.</summary>
public sealed record SnapshotTiming(
    TimeSpan Fetch,
    TimeSpan Write,
    TimeSpan Pointer,
    TimeSpan Total,
    bool UsedCompression,
    bool WasIncremental,
    int ReusedCount,
    int FetchedCount,
    int Connections);

public sealed record SnapshotResult(Snapshot Snapshot, SnapshotTiming Timing);
