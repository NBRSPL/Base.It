using Base.It.Core.Models;

namespace Base.It.Core.Abstractions;

/// <summary>
/// Reads SQL object definitions from a live database.
/// Implementations must be pure, async, cancellable, and free of UI concerns.
/// </summary>
public interface IObjectScripter
{
    Task<SqlObjectType> GetObjectTypeAsync(
        string connectionString,
        ObjectIdentifier id,
        CancellationToken ct = default);

    Task<SqlObject?> GetObjectAsync(
        string connectionString,
        ObjectIdentifier id,
        CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="GetObjectAsync"/>, but when <paramref name="id"/>
    /// points to a table the returned <see cref="SqlObject.Definition"/>
    /// is the full DACPAC-shaped script — columns with identity/defaults/
    /// computed, inline PK/UQ/CHECK, <c>CREATE INDEX</c> for every
    /// non-constraint index, <c>ALTER TABLE ADD CONSTRAINT</c> for foreign
    /// keys, and <c>CREATE TRIGGER</c> blocks for each bound trigger —
    /// intended for writing to an SSDT .sqlproj folder. For non-table
    /// objects the behaviour is identical to <see cref="GetObjectAsync"/>.
    /// </summary>
    Task<SqlObject?> GetObjectForDacpacAsync(
        string connectionString,
        ObjectIdentifier id,
        CancellationToken ct = default);

    /// <summary>
    /// Lists every user-authored syncable object in the database behind
    /// <paramref name="connectionString"/>: procedures, functions, views,
    /// tables, and triggers. Used by the Watch pane when a group doesn't
    /// pin a specific object list. Read-only, lock-free catalog query.
    /// </summary>
    Task<IReadOnlyList<SqlObjectRef>> ListAllAsync(
        string connectionString,
        CancellationToken ct = default);
}

/// <summary>Lightweight (identity + type) pair returned by <see cref="IObjectScripter.ListAllAsync"/>.</summary>
public sealed record SqlObjectRef(Base.It.Core.Models.ObjectIdentifier Id, Base.It.Core.Models.SqlObjectType Type);
