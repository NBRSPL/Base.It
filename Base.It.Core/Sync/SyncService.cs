using Base.It.Core.Abstractions;
using Base.It.Core.Backup;
using Base.It.Core.Logging;
using Base.It.Core.Models;
using Base.It.Core.Parsing;
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
        string? runStamp = null)
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

            await using (var conn = new SqlConnection(targetConn))
            {
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
}
