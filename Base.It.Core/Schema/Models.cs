using Base.It.Core.Models;

namespace Base.It.Core.Schema;

/// <summary>
/// One object in a snapshot. The hash is what makes the storage
/// content-addressable: identical definitions across snapshots collapse
/// to the same <c>objects/{hash}.sql.gz</c> file.
///
/// <see cref="ModifiedAtUtc"/> mirrors <c>sys.objects.modify_date</c>
/// from SQL Server. The next snapshot uses this to decide whether to
/// re-fetch an object's definition or trust the existing hash —
/// unchanged objects skip the network entirely. Optional so snapshots
/// taken before this field was added still load (they'll just always
/// re-fetch, which degrades them to the bulk path).
/// </summary>
public sealed record SnapshotEntry(
    string Schema,
    string Name,
    SqlObjectType Kind,
    string Hash,
    int Size,                             // bytes of the raw (uncompressed) CREATE script
    DateTime? ModifiedAtUtc = null,

    // Parent-table linkage. Populated only for triggers (kind=Trigger):
    // the (schema, name) of the table the trigger is attached to via
    // sys.triggers.parent_id. Lets the table-preview UI surface a
    // "Triggers on this table" list without having to grep the CREATE
    // TRIGGER text for `ON [schema].[table]`. Optional + nullable so
    // snapshots taken before this field existed keep loading; for
    // legacy snapshots the trigger list is simply empty for tables.
    string? ParentSchema = null,
    string? ParentName   = null)
{
    public string FullName => $"{Schema}.{Name}";

    /// <summary>Case-insensitive identity key for set operations between snapshots.</summary>
    public string Key => $"{Schema.ToUpperInvariant()}.{Name.ToUpperInvariant()}";

    /// <summary>True only for trigger entries that know their parent table.</summary>
    public bool HasParent =>
        Kind == SqlObjectType.Trigger
        && !string.IsNullOrEmpty(ParentSchema)
        && !string.IsNullOrEmpty(ParentName);
}

/// <summary>
/// A point-in-time photograph of one database's schema. Tiny on disk
/// (~100 KB for thousands of objects) because each entry is just
/// {name, kind, hash, size} — the actual SQL lives in
/// <c>objects/{hash}.sql.gz</c> shared across every snapshot that
/// references it.
///
/// <see cref="Name"/> is an optional human-readable label like
/// "sprint-12-baseline" or "before-payment-refactor". When null,
/// snapshots are identified by their UTC timestamp. The name is the
/// only piece of snapshot metadata the user can edit after capture.
/// </summary>
public sealed record Snapshot(
    string Id,                                // "20260514T103015Z"
    DateTime TakenAtUtc,
    string Environment,                       // logical env name from connection config
    string Database,
    IReadOnlyList<SnapshotEntry> Entries,
    string? Name = null);

/// <summary>Lightweight header for the snapshots list — avoids loading every entry just to render a row.</summary>
public sealed record SnapshotSummary(
    string Id,
    DateTime TakenAtUtc,
    int ObjectCount,
    long TotalRawBytes,                       // sum of every entry's Size
    string FilePath,                          // for "open in explorer"
    string? Name = null)
{
    /// <summary>
    /// Friendly label: the user's <see cref="Name"/> when set, else the
    /// UTC timestamp in fixed-width form. Used by every dropdown / list
    /// that surfaces snapshots.
    /// </summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? TakenAtUtc.ToString("yyyy-MM-dd HH:mm:ss") + " UTC"
        : Name!;
}

/// <summary>One object changed between two snapshots.</summary>
public sealed record SnapshotChange(SnapshotEntry From, SnapshotEntry To);

/// <summary>
/// Result of diffing two snapshots. Treats entries by their <c>Key</c>
/// (case-insensitive schema.name) so renames look like an Add + Remove,
/// not a Change. That matches reality — a rename in SQL Server *is*
/// a new object + a dropped one.
/// </summary>
public sealed record SnapshotDiff(
    string FromId,
    string ToId,
    IReadOnlyList<SnapshotEntry> Added,
    IReadOnlyList<SnapshotEntry> Removed,
    IReadOnlyList<SnapshotChange> Changed)
{
    public int TotalChanges => Added.Count + Removed.Count + Changed.Count;
}

/// <summary>
/// Storage-level metrics for the schema store on disk. Lets the UI show
/// "how much am I saving with dedup + gzip?" so users can validate the
/// size claim without leaving the app.
/// </summary>
public sealed record StoreStats(
    int SnapshotCount,
    int UniqueObjectCount,                    // distinct hashes on disk
    long ObjectsDiskBytes,                    // sum of *.sql.gz sizes
    long ObjectsRawBytes)                     // sum of decompressed sizes
{
    public double CompressionRatio => ObjectsRawBytes == 0
        ? 1.0
        : (double)ObjectsDiskBytes / ObjectsRawBytes;
}
