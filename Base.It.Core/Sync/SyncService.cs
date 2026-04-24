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

    public async Task<SyncResult> SyncAsync(
        string sourceConn, string targetConn,
        ObjectIdentifier id,
        string sourceEnv, string targetEnv,
        CancellationToken ct = default,
        bool zipPair = true)
    {
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
                    targetBackup = _backups.WriteObject(targetEnv, existing.Type, id, existing.Definition);
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

            var sourceBackup = _backups.WriteObject(sourceEnv, source.Type, id, source.Definition);

            // Name the zip after the object actually being synced — makes
            // a folder full of zips self-describing without opening them.
            // Batch callers pass zipPair=false and aggregate one zip at
            // the end of the run (see BatchViewModel).
            string? zipPath = null;
            if (zipPair)
            {
                var stamp   = DateTime.Now.ToString("yyyyMMddHHmmssfff");
                var zipName = $"{id.Name}_{sourceEnv}_to_{targetEnv}_{stamp}.zip";
                var zipPaths = targetBackup is null
                    ? new[] { sourceBackup }
                    : new[] { sourceBackup, targetBackup };
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
