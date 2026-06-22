namespace Base.It.Core.Sql;

/// <summary>
/// Public bag describing the full DDL shape of one user table:
/// columns, key constraints, check constraints, foreign keys, non-key
/// indexes, plus header bits (filegroup, db collation).
///
/// The inner record types live on <see cref="TableScriptRenderer"/> as
/// public nested records — they're the same shapes the renderer
/// consumes, so there's exactly one source of truth for the table
/// vocabulary. This bag exists so callers (the snapshot store, the
/// ALTER planner) can pass a single object around instead of a long
/// argument list.
/// </summary>
public sealed record TableMetadata(
    string Schema,
    string Name,
    string Filegroup,
    IReadOnlyList<TableScriptRenderer.ColumnInfo>           Columns,
    IReadOnlyList<TableScriptRenderer.KeyConstraintGroup>   KeyConstraints,
    IReadOnlyList<TableScriptRenderer.CheckConstraintInfo>  CheckConstraints,
    IReadOnlyList<TableScriptRenderer.ForeignKeyGroup>      ForeignKeys,
    IReadOnlyList<TableScriptRenderer.IndexGroup>           Indexes,
    string? DatabaseCollation);
