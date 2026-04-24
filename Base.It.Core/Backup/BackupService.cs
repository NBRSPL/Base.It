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
/// Backup-only operation (no ALTER, no execute). Captures the current
/// definition of an object from a single environment and writes it to the
/// FileBackupStore using the date/object layout.
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

    public async Task<BackupOutcome> BackupAsync(
        string connectionString, string environment, ObjectIdentifier id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return new BackupOutcome(BackupOutcomeKind.Failed, id, environment, SqlObjectType.Unknown,
                null, "No connection string.");

        try
        {
            var obj = await _scripter.GetObjectAsync(connectionString, id, ct);
            if (obj is null)
                return new BackupOutcome(BackupOutcomeKind.NotFound, id, environment, SqlObjectType.Unknown,
                    null, $"{id} not found in {environment}.");

            var path = _store.WriteObject(environment, obj.Type, id, obj.Definition);
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
