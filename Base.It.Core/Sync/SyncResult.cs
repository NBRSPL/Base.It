namespace Base.It.Core.Sync;

public enum SyncStatus { Success, NotFound, Failed }

public sealed record SyncResult(
    SyncStatus Status,
    string Message,
    string? SourceBackupPath = null,
    string? TargetBackupPath = null,
    string? ZipPath = null);
