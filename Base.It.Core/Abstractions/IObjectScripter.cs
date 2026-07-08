using Base.It.Core.Models;
using Base.It.Core.Sql;

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
    /// For a trigger identifier, returns the (schema, name) of its parent
    /// table. Used by the DACPAC export step to embed a trigger inside
    /// its parent table's file when no separate trigger file already
    /// exists in the SSDT tree. Returns <c>null</c> when the identifier
    /// doesn't resolve to a trigger or its parent isn't a user table.
    /// </summary>
    Task<ObjectIdentifier?> GetTriggerParentAsync(
        string connectionString,
        ObjectIdentifier triggerId,
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

    /// <summary>
    /// Same shape as <see cref="ListAllAsync"/> but filtered to objects
    /// whose <c>sys.objects.modify_date</c> is strictly greater than
    /// <paramref name="sinceUtc"/>. Mirrors the DBA-style
    /// "modify_date &gt; DATEADD(DAY,-1,…)" pattern used to email a
    /// daily changed-objects list — same filter, done at the SQL layer
    /// so we don't drag every object across the wire on every run.
    /// User-defined data types (alias types) come from <c>sys.types</c>
    /// and are included unconditionally; that table has no modify_date.
    /// </summary>
    Task<IReadOnlyList<SqlObjectRef>> ListChangedSinceAsync(
        string connectionString,
        DateTime sinceUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Enumerate every syncable object with its <c>modify_date</c> from
    /// <c>sys.objects</c> so callers can decide server-side whether a
    /// definition needs to be re-fetched at all. One SQL round-trip. The
    /// prod-sync engine uses this to skip full definition fetches when
    /// both sides' modify_dates match its cached last-seen values —
    /// turning a 500-object run from thousands of round-trips into two.
    /// UDDTs (alias types) live in sys.types and have no modify_date;
    /// they come back with a null date and the caller treats them as
    /// "always fetch" (they're cheap to render).
    /// </summary>
    Task<IReadOnlyList<SqlObjectMetadata>> ListAllWithModifyDatesAsync(
        string connectionString,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches the full constraint-aware metadata for one user table —
    /// header, columns, PK/UQ, CHECK, FK, indexes — for diff / ALTER
    /// planning. Returns <c>null</c> when the identifier doesn't resolve
    /// to a table on this connection. Non-table identifiers return null.
    /// </summary>
    Task<TableMetadata?> FetchTableMetadataAsync(
        string connectionString,
        ObjectIdentifier id,
        CancellationToken ct = default);
}

/// <summary>Lightweight (identity + type) pair returned by <see cref="IObjectScripter.ListAllAsync"/>.</summary>
public sealed record SqlObjectRef(Base.It.Core.Models.ObjectIdentifier Id, Base.It.Core.Models.SqlObjectType Type);

/// <summary>
/// Identity + type + modify-date snapshot for the skip-if-unchanged
/// fast path. <see cref="ModifyDateUtc"/> is null when the source
/// catalog doesn't expose one (currently: alias UDDTs from sys.types).
/// </summary>
public sealed record SqlObjectMetadata(
    Base.It.Core.Models.ObjectIdentifier Id,
    Base.It.Core.Models.SqlObjectType    Type,
    DateTime?                            ModifyDateUtc);
