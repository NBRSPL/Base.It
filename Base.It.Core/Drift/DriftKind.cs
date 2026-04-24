namespace Base.It.Core.Drift;

/// <summary>
/// Outcome of comparing a single object between a source and a target
/// environment. A "change" is anything that is NOT <see cref="InSync"/>.
/// </summary>
public enum DriftKind
{
    /// <summary>Object exists in both envs and the canonical definition hashes match.</summary>
    InSync,
    /// <summary>Object exists in both envs but hashes differ — candidate for sync.</summary>
    Different,
    /// <summary>Object exists in source, missing in target — candidate for create.</summary>
    MissingInTarget,
    /// <summary>Object exists in target, missing in source — typically ignored, surfaced for visibility.</summary>
    MissingInSource,
    /// <summary>Comparison itself failed (SQL error, timeout, bad credentials). Retriable.</summary>
    Error
}
