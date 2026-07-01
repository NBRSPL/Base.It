using Base.It.Core.Abstractions;
using Base.It.Core.Backup;
using Base.It.Core.Logging;
using Base.It.Core.Models;
using Base.It.Core.Parsing;
using Base.It.Core.Sql;
using Base.It.Core.Sync.TableAlter;
using Microsoft.Data.SqlClient;

namespace Base.It.Core.Sync;

/// <summary>
/// Orchestrates a single-object sync from source to target:
///   1. Fetch source definition
///   2. Back up target if it exists
///   3. Validate the rewritten script
///   4. Execute on target
///   5. Back up source and zip both
/// Pure async, cancellable, no UI deps.
/// </summary>
public sealed class SyncService
{
    private readonly IObjectScripter _scripter;
    private readonly FileBackupStore _backups;
    private readonly FileLogger _logger;

    public SyncService(IObjectScripter scripter, FileBackupStore backups, FileLogger logger)
    {
        _scripter = scripter;
        _backups = backups;
        _logger = logger;
    }

    /// <param name="captureSourceBackup">
    /// When true (default), this call writes a source-side backup file to
    /// <see cref="FileBackupStore"/>. Pass false from a multi-target loop
    /// after the caller has captured the source backup once — otherwise
    /// you get N copies of the same source content (one per target call)
    /// in the source-env folder.
    /// </param>
    /// <param name="runStamp">
    /// Run identifier used to group all backups produced by this
    /// operation under a single <c>{runStamp}_{role}_{env}</c> folder.
    /// Pass the same stamp to every <see cref="SyncAsync"/> call within
    /// one user click so the artifacts share a folder. Null = generate
    /// a fresh stamp here (single-call use).
    /// </param>
    public async Task<SyncResult> SyncAsync(
        string sourceConn, string targetConn,
        ObjectIdentifier id,
        string sourceEnv, string targetEnv,
        CancellationToken ct = default,
        bool zipPair = true,
        bool captureSourceBackup = true,
        string? runStamp = null,
        IReadOnlyList<AlterStep>? approvedDestructiveAlters = null)
    {
        runStamp ??= FileBackupStore.NewRunStamp();
        try
        {
            var source = await _scripter.GetObjectAsync(sourceConn, id, ct);
            if (source is null)
                return new SyncResult(SyncStatus.NotFound, $"Source object {id} not found in {sourceEnv}.");

            var targetType = await _scripter.GetObjectTypeAsync(targetConn, id, ct);
            var targetExists = targetType != SqlObjectType.Unknown;

            string? targetBackup = null;
            if (targetExists)
            {
                var existing = await _scripter.GetObjectAsync(targetConn, id, ct);
                if (existing is not null)
                    targetBackup = _backups.WriteObject(runStamp, BackupRole.Target, targetEnv, existing.Type, id, existing.Definition);
            }

            // ─── User-defined types: refuse to update in place ────────────
            //
            // SQL Server has no ALTER TYPE. Changing an existing UDT (table
            // type or alias type) requires dropping every dependent object,
            // dropping the type, recreating it, and recreating the
            // dependents — far too destructive to bundle into this
            // pipeline. First-time CREATE (target doesn't exist) still runs
            // through the normal path below.
            if (targetExists &&
                (source.Type == SqlObjectType.TableType ||
                 source.Type == SqlObjectType.UserDefinedDataType))
            {
                var kind = source.Type == SqlObjectType.TableType ? "table type" : "user-defined data type";
                var msg  =
                    $"[{id.Schema}].[{id.Name}] is a {kind}. SQL Server has no ALTER TYPE, " +
                    "so changing it requires dropping every dependent object, dropping the " +
                    "type, recreating it, then recreating the dependents. Do this manually " +
                    "in SSMS or via a schema migration script — Base.It will not overwrite " +
                    "it automatically.";
                _logger.Log($"Sync {id} {sourceEnv}->{targetEnv} refused: {kind} already exists on target");
                return new SyncResult(SyncStatus.Failed, msg, TargetBackupPath: targetBackup);
            }

            // ─── Table + target-exists: ALTER path ────────────────────────
            //
            // Branches off the regular sync pipeline before script rewriting.
            // We're not sending CREATE TABLE; we're diffing live source vs
            // live target and emitting the minimal ALTER batch — wrapped in
            // a transaction inside AlterScriptBuilder so any single failure
            // rolls back every change. Triggers on this table are NEVER
            // touched: they're separate snapshot objects, and we never DROP
            // TABLE (which is the only operation that would cascade-drop
            // them). Existing rows survive — we refuse type narrowing,
            // refuse DROP COLUMN, refuse NULL→NOT NULL without a default,
            // refuse identity changes, etc. Anything that could lose data
            // sits in DestructiveSteps and only runs if the caller passed
            // matching entries in approvedDestructiveAlters.
            if (source.Type == SqlObjectType.Table && targetExists)
            {
                return await ApplyTableAlterAsync(
                    sourceConn, targetConn, id, sourceEnv, targetEnv,
                    source, targetBackup, runStamp, zipPair, captureSourceBackup,
                    approvedDestructiveAlters, ct);
            }

            var script = targetExists
                ? CreateToAlterRewriter.Rewrite(source.Definition, source.Type)
                : source.Definition;

            var validation = TSqlValidator.Validate(script);
            if (!validation.IsValid)
            {
                var err = string.Join("; ", validation.Errors);
                _logger.Log($"Sync {id} {sourceEnv}->{targetEnv} REJECTED by parser: {err}");
                return new SyncResult(SyncStatus.Failed, $"Script failed T-SQL validation: {err}",
                    TargetBackupPath: targetBackup);
            }

            // First-time CREATE TABLE arrives as a multi-batch script
            // (CREATE TABLE; GO; CREATE INDEX; GO; ALTER TABLE ADD FK; GO; …)
            // so we route it through SqlScriptRunner which honours GO.
            // Modules (SP / FN / V / TR) are still single-batch and use
            // the simpler ExecuteNonQueryAsync path. Any failure inside
            // the table runner stops further batches and surfaces the
            // first batch's error so the user sees the actual root cause.
            if (source.Type == SqlObjectType.Table)
            {
                var runner = new SqlScriptRunner(commandTimeoutSeconds: 120);
                var outcome = await runner.ExecuteAsync(script, targetConn, ct);
                if (outcome.Status != ScriptStatus.Success)
                {
                    _logger.Log($"Sync {id} {sourceEnv}->{targetEnv} failed at batch {outcome.BatchesExecuted}: {outcome.Error}");
                    return new SyncResult(SyncStatus.Failed,
                        $"SQL error after {outcome.BatchesExecuted} batch(es): {outcome.Error}",
                        TargetBackupPath: targetBackup);
                }
            }
            else
            {
                await using var conn = new SqlConnection(targetConn);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(script, conn) { CommandTimeout = 120 };
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Skip the source-side write when the caller has already
            // captured it once for this run. Avoids N identical copies
            // when one source is being pushed to N targets.
            string? sourceBackup = captureSourceBackup
                ? _backups.WriteObject(runStamp, BackupRole.Source, sourceEnv, source.Type, id, source.Definition)
                : null;

            // Name the zip after the object actually being synced — makes
            // a folder full of zips self-describing without opening them.
            // Batch callers pass zipPair=false and aggregate one zip at
            // the end of the run (see BatchViewModel).
            string? zipPath = null;
            if (zipPair)
            {
                var stamp   = DateTime.Now.ToString("yyyyMMddHHmmssfff");
                var zipName = $"{id.Name}_{sourceEnv}_to_{targetEnv}_{stamp}.zip";
                // Filter out nulls — when captureSourceBackup is false the
                // source path is null; when target didn't pre-exist there's
                // no target backup either.
                var zipPaths = new[] { sourceBackup, targetBackup }
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Cast<string>()
                    .ToArray();
                if (zipPaths.Length > 0)
                    zipPath = _backups.ZipFiles(zipName, zipPaths);
            }

            _logger.Log(zipPath is not null
                ? $"Sync {id} {sourceEnv}->{targetEnv} OK, zip={zipPath}"
                : $"Sync {id} {sourceEnv}->{targetEnv} OK (zip deferred to batch)");
            return new SyncResult(SyncStatus.Success,
                $"Sync of {id} completed.", sourceBackup, targetBackup, zipPath);
        }
        catch (SqlException ex)
        {
            _logger.Log($"Sync {id} {sourceEnv}->{targetEnv} SQL error: {ex.Message}");
            return new SyncResult(SyncStatus.Failed, $"SQL Error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Log($"Sync {id} {sourceEnv}->{targetEnv} error: {ex.Message}");
            return new SyncResult(SyncStatus.Failed, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply a pre-fetched source definition (e.g. read from a schema
    /// store snapshot's <c>objects/{hash}.sql.gz</c>) to a target,
    /// without fetching live source. Used by the Batch "snapshot
    /// source" path: the SQL is reproducible — Batch decided what to
    /// promote based on a snapshot, and Execute replays exactly that.
    ///
    /// Same target safety as <see cref="SyncAsync"/>: backs up the
    /// target's current state before touching it, rewrites
    /// CREATE→ALTER for SP / FN / V / TR when the target exists, and
    /// leaves tables alone (the CREATE will fail on existing tables
    /// rather than dropping them — same guarantee as the per-object
    /// path). No source-side backup — the source is already
    /// persisted in the schema store.
    /// </summary>
    /// <param name="sourceLabel">
    /// Human label for logs / zip filenames. Typically
    /// "<c>{env}/{db} @ snapshot {timestamp-or-name}</c>".
    /// </param>
    public async Task<SyncResult> SyncFromDefinitionAsync(
        string targetConn,
        SqlObject source,
        string sourceLabel,
        string targetEnv,
        CancellationToken ct = default,
        bool zipPair = false,
        string? runStamp = null)
    {
        runStamp ??= FileBackupStore.NewRunStamp();
        try
        {
            var targetType = await _scripter.GetObjectTypeAsync(targetConn, source.Id, ct);
            var targetExists = targetType != SqlObjectType.Unknown;

            string? targetBackup = null;
            if (targetExists)
            {
                var existing = await _scripter.GetObjectAsync(targetConn, source.Id, ct);
                if (existing is not null)
                    targetBackup = _backups.WriteObject(
                        runStamp, BackupRole.Target, targetEnv,
                        existing.Type, source.Id, existing.Definition);
            }

            // Snapshot-source ALTER isn't supported in v1: we have only
            // the rendered CREATE script from the snapshot, not the
            // structured metadata, so we can't reliably diff against the
            // live target. Refuse with a clear message that points the
            // user at the live-source path instead. Existing target data
            // is unaffected — we return before touching anything.
            if (source.Type == SqlObjectType.Table && targetExists)
            {
                _logger.Log($"SnapshotSync {source.Id} ({sourceLabel})->{targetEnv} refused: ALTER from snapshot source not supported.");
                return new SyncResult(SyncStatus.Failed,
                    "Syncing a table from a snapshot to an existing target isn't supported — ALTER planning needs live source metadata. " +
                    "On the Sync screen pick the live source endpoint and run again, or drop the target table first if you intend to recreate it.",
                    TargetBackupPath: targetBackup);
            }

            var script = targetExists
                ? CreateToAlterRewriter.Rewrite(source.Definition, source.Type)
                : source.Definition;

            var validation = TSqlValidator.Validate(script);
            if (!validation.IsValid)
            {
                var err = string.Join("; ", validation.Errors);
                _logger.Log($"SnapshotSync {source.Id} ({sourceLabel})->{targetEnv} REJECTED: {err}");
                return new SyncResult(SyncStatus.Failed,
                    $"Script failed T-SQL validation: {err}",
                    TargetBackupPath: targetBackup);
            }

            // Same multi-batch routing as SyncAsync above — tables go
            // through SqlScriptRunner so the CREATE INDEX / ALTER TABLE
            // ADD CONSTRAINT batches following the CREATE TABLE actually
            // execute (the script already arrived constraint-aware from
            // the snapshot's stored SQL).
            if (source.Type == SqlObjectType.Table)
            {
                var runner = new SqlScriptRunner(commandTimeoutSeconds: 120);
                var outcome = await runner.ExecuteAsync(script, targetConn, ct);
                if (outcome.Status != ScriptStatus.Success)
                {
                    _logger.Log($"SnapshotSync {source.Id} ({sourceLabel})->{targetEnv} failed at batch {outcome.BatchesExecuted}: {outcome.Error}");
                    return new SyncResult(SyncStatus.Failed,
                        $"SQL error after {outcome.BatchesExecuted} batch(es): {outcome.Error}",
                        TargetBackupPath: targetBackup);
                }
            }
            else
            {
                await using var conn = new SqlConnection(targetConn);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(script, conn) { CommandTimeout = 120 };
                await cmd.ExecuteNonQueryAsync(ct);
            }

            string? zipPath = null;
            if (zipPair && targetBackup is not null)
            {
                var stamp = DateTime.Now.ToString("yyyyMMddHHmmssfff");
                // Source label may include slashes; keep filename sane.
                var safeLabel = sourceLabel.Replace('/', '_').Replace('\\', '_').Replace(':', '_').Replace(' ', '_');
                var zipName = $"{source.Id.Name}_{safeLabel}_to_{targetEnv}_{stamp}.zip";
                zipPath = _backups.ZipFiles(zipName, new[] { targetBackup });
            }

            _logger.Log($"SnapshotSync {source.Id} ({sourceLabel})->{targetEnv} OK");
            return new SyncResult(SyncStatus.Success,
                $"Applied {source.Id} from snapshot.",
                SourceBackupPath: null,
                TargetBackupPath: targetBackup,
                ZipPath:           zipPath);
        }
        catch (SqlException ex)
        {
            _logger.Log($"SnapshotSync {source.Id} ({sourceLabel})->{targetEnv} SQL error: {ex.Message}");
            return new SyncResult(SyncStatus.Failed, $"SQL Error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Log($"SnapshotSync {source.Id} ({sourceLabel})->{targetEnv} error: {ex.Message}");
            return new SyncResult(SyncStatus.Failed, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Build the ALTER plan for one table without executing anything.
    /// Single-execute callers invoke this first so the user can review
    /// the proposed changes (and approve / reject destructive ones)
    /// before <see cref="SyncAsync"/> applies the result.
    ///
    /// Returns <c>null</c> when either side's table metadata couldn't be
    /// read — e.g. the identifier points at a non-table object, or the
    /// table doesn't exist on one side. Callers treat null as "fall
    /// back to the regular CREATE path."
    /// </summary>
    public async Task<AlterPlan?> PlanTableAlterAsync(
        string sourceConn, string targetConn,
        ObjectIdentifier id,
        CancellationToken ct = default)
    {
        var sourceMeta = await _scripter.FetchTableMetadataAsync(sourceConn, id, ct);
        if (sourceMeta is null) return null;
        var targetMeta = await _scripter.FetchTableMetadataAsync(targetConn, id, ct);
        if (targetMeta is null) return null;
        return TableAlterPlanner.Plan(sourceMeta, targetMeta);
    }

    /// <summary>
    /// Inner helper for the ALTER path of <see cref="SyncAsync"/>. Builds
    /// the plan, decides what to apply (safe ∪ approved-destructive),
    /// runs the resulting transaction-wrapped batch on the target, and
    /// writes the source-side backup at the end so a successful run
    /// leaves the canonical "before / after" pair on disk.
    ///
    /// Backs out cleanly on any failure: the SQL batch is one
    /// transaction so a SqlException leaves the table untouched, and we
    /// surface the original error to the caller verbatim (no swallowing).
    /// </summary>
    private async Task<SyncResult> ApplyTableAlterAsync(
        string sourceConn, string targetConn,
        ObjectIdentifier id,
        string sourceEnv, string targetEnv,
        SqlObject source,
        string? targetBackup,
        string runStamp,
        bool zipPair, bool captureSourceBackup,
        IReadOnlyList<AlterStep>? approvedDestructiveAlters,
        CancellationToken ct)
    {
        // Re-build the plan inside the sync call so we're acting on the
        // CURRENT state of both ends, not whatever the preview saw N
        // seconds ago. The approved-destructive list from the caller is
        // matched by structural equality (records compare by field) —
        // if the table changed since the preview, unmatched approvals
        // simply don't apply and the user sees only the still-current
        // safe subset run.
        var sourceMeta = await _scripter.FetchTableMetadataAsync(sourceConn, id, ct);
        var targetMeta = await _scripter.FetchTableMetadataAsync(targetConn, id, ct);
        if (sourceMeta is null || targetMeta is null)
        {
            return new SyncResult(SyncStatus.Failed,
                "Could not read table metadata from source or target — refusing to ALTER.",
                TargetBackupPath: targetBackup);
        }

        var plan = TableAlterPlanner.Plan(sourceMeta, targetMeta);
        if (plan.IsEmpty)
        {
            return new SyncResult(SyncStatus.Success,
                $"{id} is already in sync — no ALTER needed.",
                TargetBackupPath: targetBackup,
                AlterPlan: plan);
        }

        // Resolve which destructive steps were approved. Use record
        // equality (AlterStep is a record), so identical Kind+Sql+Summary
        // → match. A step that was approved against a stale plan and is
        // no longer in DestructiveSteps quietly drops out — the caller
        // gets it back via SkippedDestructiveCount = full destructive
        // count minus matched.
        var approvedSet = new HashSet<AlterStep>(approvedDestructiveAlters ?? Array.Empty<AlterStep>());
        var destructiveToApply = plan.DestructiveSteps.Where(approvedSet.Contains).ToList();
        var skippedDestructive  = plan.DestructiveSteps.Count - destructiveToApply.Count;

        var stepsToApply = plan.SafeSteps.Concat(destructiveToApply).ToList();
        if (stepsToApply.Count == 0)
        {
            // Plan has only unapproved destructive — nothing safe to run.
            _logger.Log($"Sync {id} {sourceEnv}->{targetEnv}: {plan.DestructiveSteps.Count} destructive step(s) detected, none approved — nothing applied.");
            return new SyncResult(SyncStatus.Success,
                $"{plan.DestructiveSteps.Count} destructive change(s) detected for {id}. Nothing applied — review on the Sync screen.",
                TargetBackupPath: targetBackup,
                AlterPlan: plan,
                SkippedDestructiveCount: skippedDestructive);
        }

        var script = AlterScriptBuilder.Build(id, stepsToApply);
        var validation = TSqlValidator.Validate(script);
        if (!validation.IsValid)
        {
            var err = string.Join("; ", validation.Errors);
            _logger.Log($"Sync {id} {sourceEnv}->{targetEnv} ALTER REJECTED by parser: {err}");
            return new SyncResult(SyncStatus.Failed,
                $"ALTER script failed T-SQL validation: {err}",
                TargetBackupPath: targetBackup,
                AlterPlan: plan);
        }

        try
        {
            // Single batch on a single connection — atomic via the
            // BEGIN TRY / BEGIN TRAN / COMMIT / ROLLBACK that
            // AlterScriptBuilder wraps the steps in. CommandTimeout is
            // bumped to 300s because index rebuilds on large tables can
            // exceed the 120s default we use for module objects.
            await using var conn = new SqlConnection(targetConn);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(script, conn) { CommandTimeout = 300 };
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex)
        {
            _logger.Log($"Sync {id} {sourceEnv}->{targetEnv} ALTER SQL error: {ex.Message}");
            return new SyncResult(SyncStatus.Failed,
                $"ALTER rolled back — SQL error: {ex.Message}",
                TargetBackupPath: targetBackup,
                AlterPlan: plan);
        }

        // Source-side backup of the live source's rendered table SQL.
        // Same convention as the module path — auditable pair on disk.
        string? sourceBackup = captureSourceBackup
            ? _backups.WriteObject(runStamp, BackupRole.Source, sourceEnv, source.Type, id, source.Definition)
            : null;

        string? zipPath = null;
        if (zipPair)
        {
            var stamp   = DateTime.Now.ToString("yyyyMMddHHmmssfff");
            var zipName = $"{id.Name}_{sourceEnv}_to_{targetEnv}_ALTER_{stamp}.zip";
            var zipPaths = new[] { sourceBackup, targetBackup }
                .Where(p => !string.IsNullOrEmpty(p))
                .Cast<string>()
                .ToArray();
            if (zipPaths.Length > 0) zipPath = _backups.ZipFiles(zipName, zipPaths);
        }

        var safe = plan.SafeSteps.Count;
        var appliedDest = destructiveToApply.Count;
        var msg = appliedDest == 0
            ? $"ALTER applied: {safe} safe change(s)."
            : $"ALTER applied: {safe} safe + {appliedDest} approved destructive change(s).";
        if (skippedDestructive > 0)
            msg += $" {skippedDestructive} destructive change(s) skipped.";

        _logger.Log(zipPath is not null
            ? $"Sync {id} {sourceEnv}->{targetEnv} ALTER OK, zip={zipPath}"
            : $"Sync {id} {sourceEnv}->{targetEnv} ALTER OK");

        return new SyncResult(SyncStatus.Success, msg,
            SourceBackupPath: sourceBackup,
            TargetBackupPath: targetBackup,
            ZipPath: zipPath,
            AlterPlan: plan,
            SkippedDestructiveCount: skippedDestructive);
    }
}
