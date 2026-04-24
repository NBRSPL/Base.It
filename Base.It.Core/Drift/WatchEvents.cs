namespace Base.It.Core.Drift;

/// <summary>
/// Event flowing out of <see cref="ChangeWatcher"/>. Closed hierarchy:
/// <see cref="TickStarted"/> (once per tick), then N
/// <see cref="ObjectDrifted"/> (one per object as it finishes), then
/// <see cref="TickCompleted"/> (aggregates). Consumers pattern-match on
/// the concrete type.
/// </summary>
public abstract record WatchEvent(string SourceEnv, string TargetEnv);

/// <summary>Emitted at the start of a tick. <see cref="PlannedCount"/> is the number of objects the tick intends to compare.</summary>
public sealed record TickStarted(
    string SourceEnv, string TargetEnv, DateTime CapturedAt, int PlannedCount)
    : WatchEvent(SourceEnv, TargetEnv);

/// <summary>Emitted as each object's pair of fetches completes. The UI upserts a row per event.</summary>
public sealed record ObjectDrifted(
    string SourceEnv, string TargetEnv, ObjectDrift Drift)
    : WatchEvent(SourceEnv, TargetEnv);

/// <summary>
/// Emitted after the last <see cref="ObjectDrifted"/> of the tick, carrying
/// the aggregate counts and a <see cref="DriftBatch"/> for consumers that
/// want the legacy "whole picture" view.
/// </summary>
public sealed record TickCompleted(
    string SourceEnv, string TargetEnv, DateTime CapturedAt,
    int Total, int Changed, int Errors, DriftBatch Batch)
    : WatchEvent(SourceEnv, TargetEnv);
