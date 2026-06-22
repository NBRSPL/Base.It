using Base.It.Core.Models;

namespace Base.It.Core.Sync.TableAlter;

/// <summary>
/// One discrete change the planner proposes to apply (or refuse) when
/// syncing a table that already exists on the target. Steps are
/// emitted in a deterministic dependency order by
/// <see cref="AlterScriptBuilder"/> — drops before adds, FKs/indexes
/// out of the way before column changes, etc.
/// </summary>
public sealed record AlterStep(
    AlterStepKind Kind,
    string        Sql,
    string        Summary,
    bool          IsDestructive,
    string?       DestructiveReason);

/// <summary>
/// Phase tags drive ordering inside <see cref="AlterScriptBuilder"/>.
/// Numeric values double as the sort key — lower runs first.
///
/// Why a single linear ordering instead of a graph? Because SQL Server's
/// rule of thumb maps cleanly onto phases: drop everything that
/// references the bits we're changing, change them, re-add. Within a
/// phase, order doesn't matter (no cross-step dependency at the same
/// level). One enum + one stable sort = correct, with the dependency
/// rules visible at a glance.
/// </summary>
public enum AlterStepKind
{
    // ─── Tear-downs (in dependency order) ──────────────────────────────
    DropForeignKey      = 10,   // FK uniqueness depends on a UQ/PK → drop FK first
    DropCheckConstraint = 20,
    DropUniqueOrPk      = 30,   // before column changes, in case the col is in the key
    DropIndex           = 40,   // before column changes, same reason
    DropDefault         = 50,   // before ALTER COLUMN (a default can block type change)
    DropColumn          = 60,   // destructive — usually skipped, but if approved it runs here

    // ─── In-place column changes ───────────────────────────────────────
    AlterColumn         = 70,   // type / nullability / collation widening

    // ─── Build-ups ─────────────────────────────────────────────────────
    AddColumn           = 80,   // new column on the table itself
    AddDefault          = 90,   // attach default to existing/new column
    AddIndex            = 100,
    AddUniqueOrPk       = 110,
    AddCheckConstraint  = 120,
    AddForeignKey       = 130,
}

/// <summary>
/// The output of comparing one source table to its target — a partition
/// of all required changes into "safe to apply automatically" and
/// "would destroy data or could fail."
///
/// <list type="bullet">
///   <item><see cref="SafeSteps"/> are always applied (single mode after
///         preview; batch mode without prompt).</item>
///   <item><see cref="DestructiveSteps"/> are NEVER applied by batch
///         mode. In single mode they appear in the preview with a
///         per-step Apply checkbox (default unchecked).</item>
/// </list>
///
/// <para>Empty plans (zero safe + zero destructive) signal "already in
/// sync" — the caller can short-circuit the sync and report success
/// without running any SQL.</para>
/// </summary>
public sealed record AlterPlan(
    ObjectIdentifier Table,
    IReadOnlyList<AlterStep> SafeSteps,
    IReadOnlyList<AlterStep> DestructiveSteps)
{
    public bool IsEmpty            => SafeSteps.Count == 0 && DestructiveSteps.Count == 0;
    public bool HasDestructive     => DestructiveSteps.Count > 0;
    public bool HasSafeOnly        => SafeSteps.Count > 0 && DestructiveSteps.Count == 0;

    /// <summary>Human-readable summary suitable for status text / logs.</summary>
    public string OneLineSummary() =>
        (SafeSteps.Count, DestructiveSteps.Count) switch
        {
            (0, 0) => "Already in sync — no ALTER needed.",
            (var s, 0) => $"{s} safe change(s) ready to apply.",
            (0, var d) => $"{d} destructive change(s) detected — none applied without confirmation.",
            (var s, var d) => $"{s} safe + {d} destructive change(s) detected.",
        };
}
