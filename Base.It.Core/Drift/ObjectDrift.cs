using Base.It.Core.Models;

namespace Base.It.Core.Drift;

/// <summary>
/// The comparison result for a single object. Carries enough information to
/// feed <c>SyncService.SyncAsync</c> directly — no second fetch required to
/// decide whether to sync or skip.
/// </summary>
public sealed record ObjectDrift(
    ObjectIdentifier Id,
    DriftKind        Kind,
    SqlObjectType    SourceType,
    SqlObjectType    TargetType,
    string?          SourceHash,
    string?          TargetHash,
    string?          Message = null)
{
    /// <summary>True when this drift represents a change that can be pushed source→target.</summary>
    public bool IsSyncable => Kind is DriftKind.Different or DriftKind.MissingInTarget;
}

/// <summary>
/// A batch of comparisons captured at a single point in time. The watcher
/// emits one of these per tick; per-request callers get one back from
/// <c>DriftDetector.CompareAsync</c>.
/// </summary>
public sealed record DriftBatch(
    string                     SourceEnv,
    string                     TargetEnv,
    DateTime                   CapturedAt,
    IReadOnlyList<ObjectDrift> Items)
{
    public IEnumerable<ObjectDrift> Changed => Items.Where(i => i.IsSyncable);
    public int TotalCount   => Items.Count;
    public int ChangedCount => Items.Count(i => i.IsSyncable);
    public int ErrorCount   => Items.Count(i => i.Kind == DriftKind.Error);
}
