using Base.It.Core.Sync.TableAlter;

namespace Base.It.Core.Sync;

public enum SyncStatus { Success, NotFound, Failed }

/// <summary>
/// Outcome of one sync call.
/// <para>
/// <see cref="AlterPlan"/> is populated when the object was a Table that
/// already existed on the target — it lets the caller surface "what we
/// did and what we skipped" without re-running the differ. Null for
/// every other code path (modules, CREATE-only tables, snapshot-source
/// tables that still use the CREATE path in v1).
/// </para>
/// <para>
/// <see cref="SkippedDestructiveCount"/> is how many destructive steps
/// the planner saw but the caller didn't approve — Batch always sets
/// this to <c>DestructiveSteps.Count</c>, the single-execute path sets
/// it to <c>DestructiveSteps.Count − approvedSubset.Count</c>.
/// </para>
/// </summary>
public sealed record SyncResult(
    SyncStatus Status,
    string Message,
    string? SourceBackupPath = null,
    string? TargetBackupPath = null,
    string? ZipPath = null,
    AlterPlan? AlterPlan = null,
    int SkippedDestructiveCount = 0);
