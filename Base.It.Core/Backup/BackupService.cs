using Base.It.Core.Abstractions;
using Base.It.Core.Logging;
using Base.It.Core.Models;

namespace Base.It.Core.Backup;

public enum BackupOutcomeKind { Written, NotFound, Failed }

public sealed record BackupOutcome(
    BackupOutcomeKind Kind,
    ObjectIdentifier  Id,
    string            Environment,
    SqlObjectType     Type,
    string?           FilePath,
    string            Message);

/// <summary>
/// Backup-only operation (no ALTER, no execute). Captures an object's
/// current definition from one environment and writes it to the
/// <see cref="FileBackupStore"/> using the run-grouped layout.
/// </summary>
public sealed class BackupService
{
    private readonly IObjectScripter _scripter;
    private readonly FileBackupStore _store;
    private readonly FileLogger _logger;

    public BackupService(IObjectScripter scripter, FileBackupStore store, FileLogger logger)
    {
        _scripter = scripter; _store = store; _logger = logger;
    }

    /// <param name="role">
    /// Whether this capture represents the source of an upcoming sync,
    /// the pre-state of a target, or a standalone manual capture. Drives
    /// which subfolder the backup lands in
    /// (<c>{runStamp}_{role}_{env}</c>).
    /// </param>
    /// <param name="runStamp">
    /// Run identifier used to group every backup produced in one click
    /// under a single folder. Pass the same stamp to every
    /// <see cref="BackupAsync"/> call within one operation. Null = a
    /// fresh stamp is generated for this single call.
    /// </param>
    public async Task<BackupOutcome> BackupAsync(
        string connectionString, string environment, ObjectIdentifier id,
        BackupRole role = BackupRole.Manual,
        string? runStamp = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return new BackupOutcome(BackupOutcomeKind.Failed, id, environment, SqlObjectType.Unknown,
                null, "No connection string.");

        runStamp ??= FileBackupStore.NewRunStamp();

        try
        {
            var obj = await _scripter.GetObjectAsync(connectionString, id, ct);
            if (obj is null)
                return new BackupOutcome(BackupOutcomeKind.NotFound, id, environment, SqlObjectType.Unknown,
                    null, $"{id} not found in {environment}.");

            var path = _store.WriteObject(runStamp, role, environment, obj.Type, id, obj.Definition);
            _logger.Log($"Backup {id} [{environment}] -> {path}");
            return new BackupOutcome(BackupOutcomeKind.Written, id, environment, obj.Type, path,
                $"Saved to {path}");
        }
        catch (Exception ex)
        {
            _logger.Log($"Backup {id} [{environment}] FAILED: {ex.Message}");
            return new BackupOutcome(BackupOutcomeKind.Failed, id, environment, SqlObjectType.Unknown,
                null, ex.Message);
        }
    }
}
