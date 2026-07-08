using Base.It.Core.Abstractions;
using Base.It.Core.Hashing;
using Base.It.Core.Models;
using Microsoft.Data.SqlClient;

namespace Base.It.Core.Sql;

/// <summary>
/// Reads object metadata and definitions from SQL Server using parameterised,
/// schema-aware catalog queries. No dynamic SQL, no hardcoded 'dbo' schema.
/// Every query is prepended with <see cref="NonBlockingPreamble"/> so this
/// class never blocks another session on a lock — essential for the Watch
/// poller which runs continuously in the background.
/// </summary>
public sealed class SqlObjectScripter : IObjectScripter
{
    /// <summary>
    /// Prepended to every catalog query. Reads the uncommitted copy of
    /// metadata (safe — we hash definitions, not live business data) and
    /// caps any incidental lock wait at 2 s so nothing this class does
    /// can hold up a writer.
    /// </summary>
    private const string NonBlockingPreamble =
        "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n" +
        "SET LOCK_TIMEOUT 2000;\n";

    private const string TypeQuery = NonBlockingPreamble + @"
SELECT TOP 1 o.type
FROM sys.objects o
WHERE o.name = @name
  AND SCHEMA_NAME(o.schema_id) = @schema";

    private const string ModuleDefinitionQuery = NonBlockingPreamble + @"
SELECT sm.definition
FROM sys.sql_modules sm
INNER JOIN sys.objects o ON sm.object_id = o.object_id
WHERE o.name = @name
  AND SCHEMA_NAME(o.schema_id) = @schema";

    // Rich column query: identity, defaults, computed, collation, rowguidcol.
    // LEFT JOINs mean a plain column returns nulls in the extra fields —
    // the scripter branches on those. ic.is_not_for_replication is what
    // surfaces the "IDENTITY (..) NOT FOR REPLICATION" clause that SSMS
    // emits and DACPAC otherwise dropped.
    private const string TableColumnsQuery = NonBlockingPreamble + @"
SELECT
    c.column_id,
    c.name,
    ty.name                          AS type_name,
    c.max_length,
    c.precision,
    c.scale,
    c.is_nullable,
    c.is_identity,
    CAST(ic.seed_value      AS BIGINT) AS identity_seed,
    CAST(ic.increment_value AS BIGINT) AS identity_increment,
    ic.is_not_for_replication         AS identity_not_for_replication,
    cc.definition                     AS computed_definition,
    cc.is_persisted                   AS computed_is_persisted,
    dc.name                           AS default_name,
    dc.definition                     AS default_definition,
    c.collation_name,
    c.is_rowguidcol
FROM sys.columns c
INNER JOIN sys.tables t           ON c.object_id = t.object_id
INNER JOIN sys.types  ty          ON c.user_type_id = ty.user_type_id
LEFT JOIN sys.identity_columns   ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
LEFT JOIN sys.computed_columns   cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE t.name = @name
  AND SCHEMA_NAME(t.schema_id) = @schema
ORDER BY c.column_id";

    // Primary-key and unique constraints. Joined through sys.indexes so we
    // pick up CLUSTERED / NONCLUSTERED, column order, fill factor, padding,
    // and the backing filegroup — all of which SSDT emits in the constraint.
    private const string TableKeyConstraintsQuery = NonBlockingPreamble + @"
SELECT
    kc.name               AS constraint_name,
    kc.type               AS constraint_type,   -- 'PK' or 'UQ'
    i.type_desc           AS index_type,
    i.fill_factor         AS fill_factor,
    i.is_padded           AS is_padded,
    ds.name               AS data_space_name,
    ic.key_ordinal,
    col.name              AS column_name,
    ic.is_descending_key
FROM sys.key_constraints kc
INNER JOIN sys.indexes   i   ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
INNER JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
INNER JOIN sys.columns   col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
INNER JOIN sys.tables    t   ON t.object_id = kc.parent_object_id
WHERE t.name = @name
  AND SCHEMA_NAME(t.schema_id) = @schema
ORDER BY kc.name, ic.key_ordinal";

    // Real schema + name (catalog casing) and the heap-or-clustered
    // filegroup. Emitting table names with the stored casing avoids the
    // 'prod_suppl' vs 'prod_Suppl' mismatch between our output and SSDT's.
    private const string TableHeaderQuery = NonBlockingPreamble + @"
SELECT
    SCHEMA_NAME(t.schema_id) AS schema_name,
    t.name                    AS table_name,
    ds.name                   AS filegroup_name
FROM sys.tables t
INNER JOIN sys.indexes    i  ON i.object_id = t.object_id AND i.index_id IN (0, 1)
INNER JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
WHERE t.name = @name
  AND SCHEMA_NAME(t.schema_id) = @schema";

    private const string DatabaseCollationQuery =
        "SELECT CONVERT(NVARCHAR(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))";

    // Table- and column-level check constraints. parent_column_id = 0 for
    // table-scoped; we emit everything as a named CONSTRAINT line in the
    // CREATE TABLE body so ordering is stable. is_not_for_replication
    // captures the "CHECK NOT FOR REPLICATION (..)" form.
    private const string TableCheckConstraintsQuery = NonBlockingPreamble + @"
SELECT cc.name, cc.definition, cc.is_not_trusted, cc.is_not_for_replication
FROM sys.check_constraints cc
INNER JOIN sys.tables t ON t.object_id = cc.parent_object_id
WHERE t.name = @name
  AND SCHEMA_NAME(t.schema_id) = @schema
ORDER BY cc.name";

    // Foreign keys — emitted as ALTER TABLE ADD CONSTRAINT after CREATE
    // TABLE since the referenced table may not yet exist in deployment.
    // is_not_for_replication captures the "FOREIGN KEY .. NOT FOR REPLICATION"
    // form used to skip enforcement under replication agents.
    private const string TableForeignKeysQuery = NonBlockingPreamble + @"
SELECT
    fk.name                              AS constraint_name,
    fk.is_not_trusted,
    fk.is_not_for_replication,
    SCHEMA_NAME(ref_t.schema_id)         AS ref_schema,
    ref_t.name                           AS ref_table,
    fkc.constraint_column_id             AS ordinal,
    col.name                             AS column_name,
    ref_col.name                         AS ref_column,
    fk.delete_referential_action_desc    AS on_delete,
    fk.update_referential_action_desc    AS on_update
FROM sys.foreign_keys        fk
INNER JOIN sys.tables        t       ON t.object_id = fk.parent_object_id
INNER JOIN sys.tables        ref_t   ON ref_t.object_id = fk.referenced_object_id
INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
INNER JOIN sys.columns       col     ON col.object_id = fkc.parent_object_id     AND col.column_id = fkc.parent_column_id
INNER JOIN sys.columns       ref_col ON ref_col.object_id = fkc.referenced_object_id AND ref_col.column_id = fkc.referenced_column_id
WHERE t.name = @name
  AND SCHEMA_NAME(t.schema_id) = @schema
ORDER BY fk.name, fkc.constraint_column_id";

    // Non-PK/UQ indexes. Emitted after CREATE TABLE as CREATE INDEX.
    // Excludes heap rows (type = 0) and constraint-backing indexes (which
    // are already emitted inline via TableKeyConstraintsQuery).
    private const string TableIndexesQuery = NonBlockingPreamble + @"
SELECT
    i.name,
    i.type_desc,
    i.is_unique,
    i.filter_definition,
    ic.key_ordinal,
    ic.index_column_id,
    ic.is_included_column,
    col.name AS column_name,
    ic.is_descending_key
FROM sys.indexes       i
INNER JOIN sys.tables  t   ON t.object_id = i.object_id
INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
INNER JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
WHERE t.name = @name
  AND SCHEMA_NAME(t.schema_id) = @schema
  AND i.is_primary_key = 0
  AND i.is_unique_constraint = 0
  AND i.type > 0
ORDER BY i.name, ic.is_included_column, ic.key_ordinal, ic.index_column_id";

    // Triggers bound to this table. Triggers have no schema_id of their
    // own — they live in their parent table's schema — so we read
    // schema_name from the parent table, not from sys.triggers. We emit
    // the original CREATE TRIGGER definition verbatim;
    // sys.sql_modules.definition preserves formatting.
    private const string TableTriggersQuery = NonBlockingPreamble + @"
SELECT tr.name, SCHEMA_NAME(t.schema_id) AS schema_name, sm.definition
FROM sys.triggers    tr
INNER JOIN sys.sql_modules sm ON sm.object_id = tr.object_id
INNER JOIN sys.tables t       ON t.object_id = tr.parent_id
WHERE t.name = @name
  AND SCHEMA_NAME(t.schema_id) = @schema
ORDER BY tr.name";

    // Plain column list for the lightweight fetch path — keeps the
    // same columns as the original drift-detection query so existing
    // hashes stay stable.
    private const string TableColumnsQuerySimple = NonBlockingPreamble + @"
SELECT c.name, ty.name AS type_name, c.max_length, c.precision, c.scale, c.is_nullable
FROM sys.columns c
INNER JOIN sys.tables t ON c.object_id = t.object_id
INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
WHERE t.name = @name
  AND SCHEMA_NAME(t.schema_id) = @schema
ORDER BY c.column_id";

    /// <summary>
    /// Every user-authored object in the database. Three sources UNIONed:
    /// <list type="number">
    ///   <item>sys.objects (excluding TT — see next entry) for U/V/P/FN/IF/TF/TR</item>
    ///   <item>sys.table_types for TT — sys.objects stores TT rows under
    ///         system-generated internal names, so we can't rely on
    ///         sys.objects.name for user-facing lookups</item>
    ///   <item>sys.types for alias UDDTs (which aren't in sys.objects at all)</item>
    /// </list>
    /// Anything the sync path doesn't know how to render surfaces as
    /// <c>SqlObjectType.Unknown</c> and the engine flags it for manual
    /// review rather than silently ignoring it.
    /// </summary>
    private const string ListAllQuery = NonBlockingPreamble + @"
SELECT SCHEMA_NAME(o.schema_id) AS schema_name, o.name, o.type
FROM sys.objects o
WHERE o.is_ms_shipped = 0
  AND o.type <> 'TT'
UNION ALL
SELECT SCHEMA_NAME(tt.schema_id) AS schema_name, tt.name, 'TT' AS type
FROM sys.table_types tt
WHERE tt.is_user_defined = 1
UNION ALL
SELECT SCHEMA_NAME(t.schema_id) AS schema_name, t.name, 'UDDT' AS type
FROM sys.types t
WHERE t.is_user_defined = 1
  AND t.is_table_type   = 0
ORDER BY schema_name, name";

    /// <summary>
    /// Fallback probe for user-defined data types (alias types). Not in
    /// <c>sys.objects</c>; needs a separate read against <c>sys.types</c>.
    /// Returns 1 when the (@schema, @name) pair resolves to a UDDT.
    /// </summary>
    private const string UserDefinedDataTypeExistsQuery = NonBlockingPreamble + @"
SELECT TOP 1 1
FROM sys.types t
WHERE t.name = @name
  AND SCHEMA_NAME(t.schema_id) = @schema
  AND t.is_user_defined = 1
  AND t.is_table_type   = 0";

    /// <summary>
    /// Fallback probe for table types. Every TT has a row in
    /// <c>sys.objects</c> too, but SQL Server names it with an internal
    /// system-generated string there — <c>sys.table_types.name</c> is
    /// where the user-facing name lives. Without this probe, a plain
    /// <c>WHERE name = @userName</c> against sys.objects misses TTs
    /// entirely.
    /// </summary>
    private const string TableTypeExistsQuery = NonBlockingPreamble + @"
SELECT TOP 1 1
FROM sys.table_types tt
WHERE tt.name = @name
  AND SCHEMA_NAME(tt.schema_id) = @schema
  AND tt.is_user_defined = 1";

    /// <summary>Resolves a table type's <c>object_id</c> so we can reuse the column / constraint queries.</summary>
    private const string TableTypeObjectIdQuery = NonBlockingPreamble + @"
SELECT tt.type_table_object_id
FROM sys.table_types tt
WHERE tt.name = @name
  AND SCHEMA_NAME(tt.schema_id) = @schema";

    /// <summary>Reads the base type + length/precision/scale/nullability for a UDDT.</summary>
    private const string UserDefinedDataTypeQuery = NonBlockingPreamble + @"
SELECT
    base_ty.name        AS base_type_name,
    t.max_length        AS max_length,
    t.precision         AS precision,
    t.scale             AS scale,
    t.is_nullable       AS is_nullable
FROM sys.types t
INNER JOIN sys.types base_ty
       ON base_ty.user_type_id = t.system_type_id
      AND base_ty.is_user_defined = 0
WHERE t.name = @name
  AND SCHEMA_NAME(t.schema_id) = @schema
  AND t.is_user_defined = 1
  AND t.is_table_type   = 0";

    /// <summary>Columns of a table type. Same shape as <see cref="TableColumnsQuery"/> but keyed by <c>object_id</c>.</summary>
    private const string TableTypeColumnsQuery = NonBlockingPreamble + @"
SELECT
    c.column_id,
    c.name,
    ty.name                          AS type_name,
    c.max_length,
    c.precision,
    c.scale,
    c.is_nullable,
    c.is_identity,
    CAST(ic.seed_value      AS BIGINT) AS identity_seed,
    CAST(ic.increment_value AS BIGINT) AS identity_increment,
    ic.is_not_for_replication         AS identity_not_for_replication,
    cc.definition                     AS computed_definition,
    cc.is_persisted                   AS computed_is_persisted,
    dc.name                           AS default_name,
    dc.definition                     AS default_definition,
    c.collation_name,
    c.is_rowguidcol
FROM sys.columns c
INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
LEFT JOIN sys.identity_columns   ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
LEFT JOIN sys.computed_columns   cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE c.object_id = @object_id
ORDER BY c.column_id";

    private const string TableTypeKeyConstraintsQuery = NonBlockingPreamble + @"
SELECT
    kc.name               AS constraint_name,
    kc.type               AS constraint_type,
    i.type_desc           AS index_type,
    i.fill_factor         AS fill_factor,
    i.is_padded           AS is_padded,
    ds.name               AS data_space_name,
    ic.key_ordinal,
    col.name              AS column_name,
    ic.is_descending_key
FROM sys.key_constraints kc
INNER JOIN sys.indexes       i   ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
INNER JOIN sys.data_spaces   ds  ON ds.data_space_id = i.data_space_id
INNER JOIN sys.index_columns ic  ON ic.object_id = i.object_id AND ic.index_id = i.index_id
INNER JOIN sys.columns       col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
WHERE kc.parent_object_id = @object_id
ORDER BY kc.name, ic.key_ordinal";

    private const string TableTypeCheckConstraintsQuery = NonBlockingPreamble + @"
SELECT cc.name, cc.definition, cc.is_not_trusted, cc.is_not_for_replication
FROM sys.check_constraints cc
WHERE cc.parent_object_id = @object_id
ORDER BY cc.name";

    public async Task<SqlObjectType> GetObjectTypeAsync(
        string connectionString, ObjectIdentifier id, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Probe sys.objects first — covers U/V/P/FN/IF/TF/TR by user-facing name.
        // Note: TT rows exist in sys.objects but with SYSTEM-GENERATED names,
        // not the user-facing name — so this probe won't catch table types
        // even though they're technically here. That's what the TT branch below
        // is for.
        await using (var cmd = new SqlCommand(TypeQuery, conn))
        {
            cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
            cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;

            var raw = await cmd.ExecuteScalarAsync(ct) as string;
            var mapped = MapSysObjectsTypeCode(raw);
            if (mapped != SqlObjectType.Unknown) return mapped;
        }

        // Table types — user-facing name lives in sys.table_types.
        await using (var ttCmd = new SqlCommand(TableTypeExistsQuery, conn))
        {
            ttCmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
            ttCmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
            var exists = await ttCmd.ExecuteScalarAsync(ct);
            if (exists is not null) return SqlObjectType.TableType;
        }

        // Alias types (CREATE TYPE ... FROM base) live in sys.types, not
        // sys.objects — fall back to a targeted probe there before giving up.
        await using (var uddtCmd = new SqlCommand(UserDefinedDataTypeExistsQuery, conn))
        {
            uddtCmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
            uddtCmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
            var exists = await uddtCmd.ExecuteScalarAsync(ct);
            return exists is not null ? SqlObjectType.UserDefinedDataType : SqlObjectType.Unknown;
        }
    }

    private static SqlObjectType MapSysObjectsTypeCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? SqlObjectType.Unknown : code.Trim().ToUpperInvariant() switch
        {
            "U"  => SqlObjectType.Table,
            "V"  => SqlObjectType.View,
            "P"  => SqlObjectType.StoredProcedure,
            "FN" => SqlObjectType.ScalarFunction,
            "IF" => SqlObjectType.InlineTableFunction,
            "TF" => SqlObjectType.TableValuedFunction,
            "TR" => SqlObjectType.Trigger,
            "TT" => SqlObjectType.TableType,
            _    => SqlObjectType.Unknown
        };

    public async Task<SqlObject?> GetObjectAsync(
        string connectionString, ObjectIdentifier id, CancellationToken ct = default)
    {
        // Tables + table types route through the constraint-aware catalog
        // path so preview / diff / sync / drift see the full definition.
        // Alias types (UDDTs) have their own render path — different DDL
        // shape entirely (CREATE TYPE ... FROM base). Everything else is
        // a module (P / V / FN / IF / TF / TR) and comes from sys.sql_modules.
        var type = await GetObjectTypeAsync(connectionString, id, ct);
        if (type == SqlObjectType.Unknown) return null;

        string definition = type switch
        {
            SqlObjectType.Table               => await ScriptTableForDacpacAsync(connectionString, id, ct),
            SqlObjectType.TableType           => await ScriptTableTypeAsync(connectionString, id, ct),
            SqlObjectType.UserDefinedDataType => await ScriptUserDefinedDataTypeAsync(connectionString, id, ct),
            _                                 => await GetModuleDefinitionAsync(connectionString, id, ct)
        };

        if (string.IsNullOrWhiteSpace(definition)) return null;
        return new SqlObject(id, type, definition, DefinitionHasher.Hash(definition));
    }

    // ─── User-defined types (TT + alias) ───────────────────────────────────

    /// <summary>
    /// Renders a table type (<c>CREATE TYPE [x] AS TABLE (...)</c>). Reuses
    /// <see cref="TableScriptRenderer"/> so column, PK/UQ, and CHECK output
    /// is byte-identical to how a real table would be scripted.
    /// </summary>
    private async Task<string> ScriptTableTypeAsync(
        string connectionString, ObjectIdentifier id, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        int? objectId;
        await using (var cmd = new SqlCommand(TableTypeObjectIdQuery, conn))
        {
            cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
            cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
            objectId = (int?)await cmd.ExecuteScalarAsync(ct);
        }
        if (objectId is null) return string.Empty;

        var dbCollation      = await LoadDatabaseCollationAsync(conn, ct);
        var columns          = await LoadTableTypeColumnsAsync(conn, objectId.Value, ct);
        if (columns.Count == 0) return string.Empty;
        var keyConstraints   = await LoadTableTypeKeyConstraintsAsync(conn, objectId.Value, ct);
        var checkConstraints = await LoadTableTypeCheckConstraintsAsync(conn, objectId.Value, ct);

        return TableScriptRenderer.RenderTableType(
            schema:           id.Schema,
            name:             id.Name,
            columns:          columns,
            keyConstraints:   keyConstraints,
            checkConstraints: checkConstraints,
            dbCollation:      dbCollation);
    }

    /// <summary>
    /// Renders an alias / user-defined data type
    /// (<c>CREATE TYPE [x] FROM basetype(len) [NOT] NULL</c>). One row of
    /// catalog metadata drives everything — no columns, no constraints.
    /// </summary>
    private async Task<string> ScriptUserDefinedDataTypeAsync(
        string connectionString, ObjectIdentifier id, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(UserDefinedDataTypeQuery, conn);
        cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return string.Empty;

        return TableScriptRenderer.RenderUserDefinedDataType(
            schema:       id.Schema,
            name:         id.Name,
            baseTypeName: reader.GetString(reader.GetOrdinal("base_type_name")),
            maxLength:    reader.GetInt16(reader.GetOrdinal("max_length")),
            precision:    reader.GetByte(reader.GetOrdinal("precision")),
            scale:        reader.GetByte(reader.GetOrdinal("scale")),
            isNullable:   reader.GetBoolean(reader.GetOrdinal("is_nullable")));
    }

    private static async Task<List<TableScriptRenderer.ColumnInfo>> LoadTableTypeColumnsAsync(
        SqlConnection conn, int objectId, CancellationToken ct)
    {
        var list = new List<TableScriptRenderer.ColumnInfo>();
        await using var cmd = new SqlCommand(TableTypeColumnsQuery, conn);
        cmd.Parameters.Add("@object_id", System.Data.SqlDbType.Int).Value = objectId;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new TableScriptRenderer.ColumnInfo(
                Name:                      reader.GetString(reader.GetOrdinal("name")),
                TypeName:                  reader.GetString(reader.GetOrdinal("type_name")),
                MaxLength:                 reader.GetInt16 (reader.GetOrdinal("max_length")),
                Precision:                 reader.GetByte  (reader.GetOrdinal("precision")),
                Scale:                     reader.GetByte  (reader.GetOrdinal("scale")),
                IsNullable:                reader.GetBoolean(reader.GetOrdinal("is_nullable")),
                IsIdentity:                reader.GetBoolean(reader.GetOrdinal("is_identity")),
                IdentitySeed:              SafeLong(reader, "identity_seed"),
                IdentityIncrement:         SafeLong(reader, "identity_increment"),
                IdentityNotForReplication: SafeBool(reader, "identity_not_for_replication") ?? false,
                ComputedDefinition:        SafeString(reader, "computed_definition"),
                ComputedIsPersisted:       SafeBool(reader, "computed_is_persisted"),
                DefaultName:               SafeString(reader, "default_name"),
                DefaultDefinition:         SafeString(reader, "default_definition"),
                CollationName:             SafeString(reader, "collation_name"),
                IsRowGuidCol:              reader.GetBoolean(reader.GetOrdinal("is_rowguidcol"))));
        }
        return list;
    }

    private static async Task<List<TableScriptRenderer.KeyConstraintGroup>> LoadTableTypeKeyConstraintsAsync(
        SqlConnection conn, int objectId, CancellationToken ct)
    {
        var rows = new List<(string Name, string Type, string IndexType, byte FillFactor,
                             bool IsPadded, string DataSpaceName, string Column, bool Desc)>();
        await using var cmd = new SqlCommand(TableTypeKeyConstraintsQuery, conn);
        cmd.Parameters.Add("@object_id", System.Data.SqlDbType.Int).Value = objectId;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((
                reader.GetString (reader.GetOrdinal("constraint_name")),
                reader.GetString (reader.GetOrdinal("constraint_type")).Trim(),
                reader.GetString (reader.GetOrdinal("index_type")),
                reader.GetByte   (reader.GetOrdinal("fill_factor")),
                reader.GetBoolean(reader.GetOrdinal("is_padded")),
                reader.GetString (reader.GetOrdinal("data_space_name")),
                reader.GetString (reader.GetOrdinal("column_name")),
                reader.GetBoolean(reader.GetOrdinal("is_descending_key"))));
        }
        return rows.GroupBy(r => r.Name)
                   .Select(g => new TableScriptRenderer.KeyConstraintGroup(
                       Name:          g.Key,
                       Type:          g.First().Type,
                       IndexType:     g.First().IndexType,
                       FillFactor:    g.First().FillFactor,
                       IsPadded:      g.First().IsPadded,
                       DataSpaceName: g.First().DataSpaceName,
                       Columns:       g.Select(r => (r.Column, r.Desc)).ToList()))
                   .ToList();
    }

    private static async Task<List<TableScriptRenderer.CheckConstraintInfo>> LoadTableTypeCheckConstraintsAsync(
        SqlConnection conn, int objectId, CancellationToken ct)
    {
        var list = new List<TableScriptRenderer.CheckConstraintInfo>();
        await using var cmd = new SqlCommand(TableTypeCheckConstraintsQuery, conn);
        cmd.Parameters.Add("@object_id", System.Data.SqlDbType.Int).Value = objectId;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new TableScriptRenderer.CheckConstraintInfo(
                Name:                reader.GetString(0),
                Definition:          reader.GetString(1),
                IsNotTrusted:        reader.GetBoolean(2),
                IsNotForReplication: reader.GetBoolean(3)));
        }
        return list;
    }

    /// <summary>
    /// Same as <see cref="GetObjectAsync"/> today. Kept as a stable public
    /// entry point for callers that semantically want the DACPAC-shaped
    /// definition (the DACPAC export flow). Both call sites end up at the
    /// same constraint-aware scripter now.
    /// </summary>
    public Task<SqlObject?> GetObjectForDacpacAsync(
        string connectionString, ObjectIdentifier id, CancellationToken ct = default)
        => GetObjectAsync(connectionString, id, ct);

    /// <summary>
    /// Lightweight column-only fetch — kept as an internal helper for the
    /// (rare) callers that intentionally want JUST the column shape, not
    /// the full constraint-aware definition. The default
    /// <see cref="GetObjectAsync"/> path no longer uses this.
    /// </summary>
    public async Task<SqlObject?> GetObjectColumnsOnlyAsync(
        string connectionString, ObjectIdentifier id, CancellationToken ct = default)
    {
        var type = await GetObjectTypeAsync(connectionString, id, ct);
        if (type == SqlObjectType.Unknown) return null;

        string definition = type == SqlObjectType.Table
            ? await ScriptTableSimpleAsync(connectionString, id, ct)
            : await GetModuleDefinitionAsync(connectionString, id, ct);

        if (string.IsNullOrWhiteSpace(definition)) return null;
        return new SqlObject(id, type, definition, DefinitionHasher.Hash(definition));
    }

    /// <summary>
    /// For a trigger, returns the (schema, name) of its parent table —
    /// triggers in SQL Server are bound to a single object via
    /// <c>sys.triggers.parent_id</c>. Returns <c>null</c> when the
    /// identifier doesn't resolve to a trigger or the parent isn't a
    /// table (e.g. database-level DDL triggers).
    /// </summary>
    public async Task<ObjectIdentifier?> GetTriggerParentAsync(
        string connectionString, ObjectIdentifier triggerId, CancellationToken ct = default)
    {
        const string Q = NonBlockingPreamble + @"
SELECT SCHEMA_NAME(t.schema_id) AS schema_name, t.name AS table_name
FROM sys.triggers tr
INNER JOIN sys.tables t ON t.object_id = tr.parent_id
WHERE tr.name = @name
  AND SCHEMA_NAME(t.schema_id) = @schema";

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(Q, conn);
        cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = triggerId.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = triggerId.Schema;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ObjectIdentifier(reader.GetString(0), reader.GetString(1));
    }

    private static async Task<string> GetModuleDefinitionAsync(
        string connectionString, ObjectIdentifier id, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(ModuleDefinitionQuery, conn);
        cmd.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
        return await cmd.ExecuteScalarAsync(ct) as string ?? string.Empty;
    }

    /// <summary>
    /// Same as <see cref="ListAllQuery"/> but filters sys.objects by
    /// <c>modify_date &gt; @sinceUtc</c>. Alias types (from sys.types)
    /// have no modify_date column, so they're always included — the
    /// caller can decide whether to keep them by inspecting the type.
    /// </summary>
    private const string ListChangedSinceQuery = NonBlockingPreamble + @"
SELECT SCHEMA_NAME(o.schema_id) AS schema_name, o.name, o.type
FROM sys.objects o
WHERE o.is_ms_shipped = 0
  AND o.type <> 'TT'
  AND o.modify_date > @since_utc
UNION ALL
-- TT names live in sys.table_types; join sys.objects on the underlying
-- schema row to get modify_date so the time filter still applies.
SELECT SCHEMA_NAME(tt.schema_id) AS schema_name, tt.name, 'TT' AS type
FROM sys.table_types tt
INNER JOIN sys.objects o ON o.object_id = tt.type_table_object_id
WHERE tt.is_user_defined = 1
  AND o.modify_date > @since_utc
UNION ALL
SELECT SCHEMA_NAME(t.schema_id) AS schema_name, t.name, 'UDDT' AS type
FROM sys.types t
WHERE t.is_user_defined = 1
  AND t.is_table_type   = 0
ORDER BY schema_name, name";

    public async Task<IReadOnlyList<SqlObjectRef>> ListAllAsync(
        string connectionString, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return Array.Empty<SqlObjectRef>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(ListAllQuery, conn) { CommandTimeout = 30 };

        var results = new List<SqlObjectRef>(capacity: 256);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);
            var name   = reader.GetString(1);
            var type   = reader.GetString(2).Trim().ToUpperInvariant();
            var sqlType = type switch
            {
                "U"    => SqlObjectType.Table,
                "V"    => SqlObjectType.View,
                "P"    => SqlObjectType.StoredProcedure,
                "FN"   => SqlObjectType.ScalarFunction,
                "IF"   => SqlObjectType.InlineTableFunction,
                "TF"   => SqlObjectType.TableValuedFunction,
                "TR"   => SqlObjectType.Trigger,
                "TT"   => SqlObjectType.TableType,
                "UDDT" => SqlObjectType.UserDefinedDataType,
                _      => SqlObjectType.Unknown
            };
            if (sqlType == SqlObjectType.Unknown) continue;
            results.Add(new SqlObjectRef(new ObjectIdentifier(schema, name), sqlType));
        }
        return results;
    }

    /// <summary>
    /// One-shot metadata dump: every user object's identity, type, and
    /// modify_date. Feeds the prod-sync engine's skip-if-unchanged
    /// fast path so per-object definition fetches are only done when
    /// something has actually moved.
    /// </summary>
    private const string ListAllWithModifyDatesQuery = NonBlockingPreamble + @"
SELECT SCHEMA_NAME(o.schema_id) AS schema_name, o.name, o.type, o.modify_date
FROM sys.objects o
WHERE o.is_ms_shipped = 0
  AND o.type <> 'TT'
UNION ALL
SELECT SCHEMA_NAME(tt.schema_id) AS schema_name, tt.name, 'TT' AS type, o.modify_date
FROM sys.table_types tt
INNER JOIN sys.objects o ON o.object_id = tt.type_table_object_id
WHERE tt.is_user_defined = 1
UNION ALL
SELECT SCHEMA_NAME(t.schema_id) AS schema_name, t.name, 'UDDT' AS type, CAST(NULL AS DATETIME) AS modify_date
FROM sys.types t
WHERE t.is_user_defined = 1
  AND t.is_table_type   = 0
ORDER BY schema_name, name";

    public async Task<IReadOnlyList<SqlObjectMetadata>> ListAllWithModifyDatesAsync(
        string connectionString, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return Array.Empty<SqlObjectMetadata>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(ListAllWithModifyDatesQuery, conn) { CommandTimeout = 60 };

        var results = new List<SqlObjectMetadata>(capacity: 512);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);
            var name   = reader.GetString(1);
            var type   = reader.GetString(2).Trim().ToUpperInvariant();
            DateTime? modifyDate = reader.IsDBNull(3)
                ? null
                : DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc);

            var sqlType = type switch
            {
                "U"    => SqlObjectType.Table,
                "V"    => SqlObjectType.View,
                "P"    => SqlObjectType.StoredProcedure,
                "FN"   => SqlObjectType.ScalarFunction,
                "IF"   => SqlObjectType.InlineTableFunction,
                "TF"   => SqlObjectType.TableValuedFunction,
                "TR"   => SqlObjectType.Trigger,
                "TT"   => SqlObjectType.TableType,
                "UDDT" => SqlObjectType.UserDefinedDataType,
                _      => SqlObjectType.Unknown
            };
            if (sqlType == SqlObjectType.Unknown) continue;
            results.Add(new SqlObjectMetadata(new ObjectIdentifier(schema, name), sqlType, modifyDate));
        }
        return results;
    }

    public async Task<IReadOnlyList<SqlObjectRef>> ListChangedSinceAsync(
        string connectionString, DateTime sinceUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return Array.Empty<SqlObjectRef>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(ListChangedSinceQuery, conn) { CommandTimeout = 30 };
        cmd.Parameters.Add("@since_utc", System.Data.SqlDbType.DateTime2).Value = sinceUtc;

        var results = new List<SqlObjectRef>(capacity: 64);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);
            var name   = reader.GetString(1);
            var type   = reader.GetString(2).Trim().ToUpperInvariant();
            var sqlType = type switch
            {
                "U"    => SqlObjectType.Table,
                "V"    => SqlObjectType.View,
                "P"    => SqlObjectType.StoredProcedure,
                "FN"   => SqlObjectType.ScalarFunction,
                "IF"   => SqlObjectType.InlineTableFunction,
                "TF"   => SqlObjectType.TableValuedFunction,
                "TR"   => SqlObjectType.Trigger,
                "TT"   => SqlObjectType.TableType,
                "UDDT" => SqlObjectType.UserDefinedDataType,
                _      => SqlObjectType.Unknown
            };
            if (sqlType == SqlObjectType.Unknown) continue;
            results.Add(new SqlObjectRef(new ObjectIdentifier(schema, name), sqlType));
        }
        return results;
    }

    /// <summary>
    /// Lightweight column-only CREATE TABLE. Used by the default fetch
    /// path (drift detection / Compare / Query fetch).
    ///
    /// Columns are emitted in case-insensitive alphabetical order — NOT
    /// the underlying sys.columns.column_id (storage) order — so that two
    /// tables with the same columns in different storage positions hash
    /// equal and don't get flagged as Different in Watch / Compare.
    /// Logical schema equality is "same columns + same types + same
    /// nullability", and physical column position isn't a schema fact.
    /// (The DACPAC export path preserves column_id order because exported
    /// SQL files have conventional declaration order; that's a different
    /// concern.)
    /// </summary>
    private static async Task<string> ScriptTableSimpleAsync(
        string connectionString, ObjectIdentifier id, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(TableColumnsQuerySimple, conn);
        cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;

        var columns = new List<(string Name, string Line)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name       = reader.GetString(0);
            var typeName   = reader.GetString(1);
            var maxLen     = reader.GetInt16(2);
            var precision  = reader.GetByte(3);
            var scale      = reader.GetByte(4);
            var isNullable = reader.GetBoolean(5);

            string typeSpec = typeName.ToLowerInvariant() switch
            {
                "varchar" or "char"    => $"{typeName}({(maxLen == -1 ? "max" : maxLen.ToString())})",
                "nvarchar" or "nchar"  => $"{typeName}({(maxLen == -1 ? "max" : (maxLen / 2).ToString())})",
                "decimal" or "numeric" => $"{typeName}({precision},{scale})",
                _                      => typeName
            };
            columns.Add((name, $"    [{name}] {typeSpec} {(isNullable ? "NULL" : "NOT NULL")}"));
        }
        if (columns.Count == 0) return string.Empty;
        var sortedLines = columns
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Line);
        return $"CREATE TABLE [{id.Schema}].[{id.Name}] (\n{string.Join(",\n", sortedLines)}\n);\n";
    }

    /// <summary>
    /// Produces a DACPAC/SSDT-shaped definition for a table by loading
    /// every catalog dependency from one connection and delegating the
    /// SQL emission to <see cref="TableScriptRenderer"/>. The renderer is
    /// shared with the bulk snapshot fetcher so the on-disk shape is
    /// identical regardless of which path produced it.
    /// </summary>
    private async Task<string> ScriptTableForDacpacAsync(
        string connectionString, ObjectIdentifier id, CancellationToken ct)
    {
        var meta = await FetchTableMetadataAsync(connectionString, id, ct);
        if (meta is null) return string.Empty;

        // Triggers stay as first-class objects: the bulk snapshot fetcher
        // captures them as type='TR', and the merged Sync screen / preview
        // shows them in their own pane. Embedding them inline in a table's
        // CREATE script would (a) duplicate them in the on-disk
        // representation, and (b) make a Compare diff on a table noisy
        // with trigger source. The Snapshots screen surfaces them as a
        // "Triggers on this table" sidebar instead. Pass an empty list.
        var triggers = Array.Empty<(string, string, string)>();

        return TableScriptRenderer.Render(
            schema:           meta.Schema,
            name:             meta.Name,
            filegroup:        meta.Filegroup,
            columns:          meta.Columns,
            keyConstraints:   meta.KeyConstraints,
            checkConstraints: meta.CheckConstraints,
            foreignKeys:      meta.ForeignKeys,
            indexes:          meta.Indexes,
            triggers:         triggers,
            dbCollation:      meta.DatabaseCollation);
    }

    /// <summary>
    /// Fetch the full constraint-aware metadata for one user table —
    /// header, columns, PK/UQ, CHECK, FK, indexes — from the live
    /// connection. Used by the ALTER planner (which needs to compare
    /// source vs target shapes column-by-column) and internally by
    /// <see cref="ScriptTableForDacpacAsync"/>, so the snapshot capture
    /// path and the ALTER diff path see identical data.
    ///
    /// Returns <c>null</c> when the table doesn't exist or has no
    /// columns (the latter shouldn't happen in practice but we guard
    /// anyway — a header without columns means a half-created table).
    /// </summary>
    public async Task<TableMetadata?> FetchTableMetadataAsync(
        string connectionString, ObjectIdentifier id, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Header (real casing + filegroup) and DB collation are read up
        // front so the body rendering can use them.
        var header      = await LoadTableHeaderAsync(conn, id, ct);
        if (header is null) return null;
        var dbCollation = await LoadDatabaseCollationAsync(conn, ct);

        // All remaining catalog queries run sequentially on one connection
        // so nothing interleaves mid-script. They're read-only + non-blocking.
        var columns   = await LoadColumnsAsync(conn, id, ct);
        if (columns.Count == 0) return null;
        var keyCons   = await LoadKeyConstraintsAsync(conn, id, ct);
        var checkCons = await LoadCheckConstraintsAsync(conn, id, ct);
        var fkeys     = await LoadForeignKeysAsync(conn, id, ct);
        var indexes   = await LoadIndexesAsync(conn, id, ct);

        return new TableMetadata(
            Schema:            header.Value.Schema,
            Name:              header.Value.Name,
            Filegroup:         header.Value.Filegroup,
            Columns:           columns,
            KeyConstraints:    keyCons,
            CheckConstraints:  checkCons,
            ForeignKeys:       fkeys,
            Indexes:           indexes,
            DatabaseCollation: dbCollation);
    }

    // ---- Table header + DB collation --------------------------------------

    private static async Task<(string Schema, string Name, string Filegroup)?> LoadTableHeaderAsync(
        SqlConnection conn, ObjectIdentifier id, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(TableHeaderQuery, conn);
        cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static async Task<string?> LoadDatabaseCollationAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(DatabaseCollationQuery, conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    // ---- Column metadata ---------------------------------------------------

    // ─── Catalog readers — populate TableScriptRenderer record types ─────
    //
    // These methods do the SQL I/O. The rendering of the result lives in
    // TableScriptRenderer and is shared with the bulk-fetch path so the
    // on-disk SQL is identical regardless of which entry point produced it.

    private static async Task<List<TableScriptRenderer.ColumnInfo>> LoadColumnsAsync(
        SqlConnection conn, ObjectIdentifier id, CancellationToken ct)
    {
        var list = new List<TableScriptRenderer.ColumnInfo>();
        await using var cmd = new SqlCommand(TableColumnsQuery, conn);
        cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new TableScriptRenderer.ColumnInfo(
                Name:                      reader.GetString(reader.GetOrdinal("name")),
                TypeName:                  reader.GetString(reader.GetOrdinal("type_name")),
                MaxLength:                 reader.GetInt16 (reader.GetOrdinal("max_length")),
                Precision:                 reader.GetByte  (reader.GetOrdinal("precision")),
                Scale:                     reader.GetByte  (reader.GetOrdinal("scale")),
                IsNullable:                reader.GetBoolean(reader.GetOrdinal("is_nullable")),
                IsIdentity:                reader.GetBoolean(reader.GetOrdinal("is_identity")),
                IdentitySeed:              SafeLong(reader, "identity_seed"),
                IdentityIncrement:         SafeLong(reader, "identity_increment"),
                IdentityNotForReplication: SafeBool(reader, "identity_not_for_replication") ?? false,
                ComputedDefinition:        SafeString(reader, "computed_definition"),
                ComputedIsPersisted:       SafeBool(reader, "computed_is_persisted"),
                DefaultName:               SafeString(reader, "default_name"),
                DefaultDefinition:         SafeString(reader, "default_definition"),
                CollationName:             SafeString(reader, "collation_name"),
                IsRowGuidCol:              reader.GetBoolean(reader.GetOrdinal("is_rowguidcol"))));
        }
        return list;
    }

    private static async Task<List<TableScriptRenderer.KeyConstraintGroup>> LoadKeyConstraintsAsync(
        SqlConnection conn, ObjectIdentifier id, CancellationToken ct)
    {
        var rows = new List<(string Name, string Type, string IndexType, byte FillFactor,
                             bool IsPadded, string DataSpaceName, string Column, bool Desc)>();
        await using var cmd = new SqlCommand(TableKeyConstraintsQuery, conn);
        cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((
                reader.GetString(reader.GetOrdinal("constraint_name")),
                reader.GetString(reader.GetOrdinal("constraint_type")).Trim(),
                reader.GetString(reader.GetOrdinal("index_type")),
                reader.GetByte  (reader.GetOrdinal("fill_factor")),
                reader.GetBoolean(reader.GetOrdinal("is_padded")),
                reader.GetString(reader.GetOrdinal("data_space_name")),
                reader.GetString(reader.GetOrdinal("column_name")),
                reader.GetBoolean(reader.GetOrdinal("is_descending_key"))));
        }
        return rows.GroupBy(r => r.Name)
                   .Select(g => new TableScriptRenderer.KeyConstraintGroup(
                       Name:          g.Key,
                       Type:          g.First().Type,
                       IndexType:     g.First().IndexType,
                       FillFactor:    g.First().FillFactor,
                       IsPadded:      g.First().IsPadded,
                       DataSpaceName: g.First().DataSpaceName,
                       Columns:       g.Select(r => (r.Column, r.Desc)).ToList()))
                   .ToList();
    }

    private static async Task<List<TableScriptRenderer.CheckConstraintInfo>> LoadCheckConstraintsAsync(
        SqlConnection conn, ObjectIdentifier id, CancellationToken ct)
    {
        var list = new List<TableScriptRenderer.CheckConstraintInfo>();
        await using var cmd = new SqlCommand(TableCheckConstraintsQuery, conn);
        cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new TableScriptRenderer.CheckConstraintInfo(
                Name:                reader.GetString(0),
                Definition:          reader.GetString(1),
                IsNotTrusted:        reader.GetBoolean(2),
                IsNotForReplication: reader.GetBoolean(3)));
        }
        return list;
    }

    private static async Task<List<TableScriptRenderer.ForeignKeyGroup>> LoadForeignKeysAsync(
        SqlConnection conn, ObjectIdentifier id, CancellationToken ct)
    {
        var rows = new List<(string Name, bool NotTrusted, bool NFR,
                             string RefSchema, string RefTable,
                             string Column, string RefColumn,
                             string OnDelete, string OnUpdate)>();
        await using var cmd = new SqlCommand(TableForeignKeysQuery, conn);
        cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9)));
        }
        return rows.GroupBy(r => r.Name)
                   .Select(g => new TableScriptRenderer.ForeignKeyGroup(
                       Name:                g.Key,
                       IsNotTrusted:        g.First().NotTrusted,
                       IsNotForReplication: g.First().NFR,
                       RefSchema:           g.First().RefSchema,
                       RefTable:            g.First().RefTable,
                       OnDelete:            g.First().OnDelete,
                       OnUpdate:            g.First().OnUpdate,
                       Columns:             g.Select(r => (r.Column, r.RefColumn)).ToList()))
                   .ToList();
    }

    private static async Task<List<TableScriptRenderer.IndexGroup>> LoadIndexesAsync(
        SqlConnection conn, ObjectIdentifier id, CancellationToken ct)
    {
        var rows = new List<(string Name, string TypeDesc, bool IsUnique, string? Filter,
                             bool IsIncluded, string Column, bool Desc)>();
        await using var cmd = new SqlCommand(TableIndexesQuery, conn);
        cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                SafeString(reader, "filter_definition"),
                reader.GetBoolean(6),
                reader.GetString(7),
                reader.GetBoolean(8)));
        }
        return rows.GroupBy(r => r.Name)
                   .Select(g => new TableScriptRenderer.IndexGroup(
                       Name:     g.Key,
                       TypeDesc: g.First().TypeDesc,
                       IsUnique: g.First().IsUnique,
                       Filter:   g.First().Filter,
                       KeyCols:     g.Where(r => !r.IsIncluded)
                                     .Select(r => (r.Column, r.Desc)).ToList(),
                       IncludeCols: g.Where(r => r.IsIncluded)
                                     .Select(r => r.Column).ToList()))
                   .ToList();
    }

    // Triggers-on-this-table loader removed — triggers are captured as
    // their own first-class objects (type='TR') and never embedded in
    // a table's CREATE script any more. The "Triggers on this table"
    // sidebar in SnapshotsView surfaces the relationship instead.
    // The TableTriggersQuery constant above is left unused but kept as
    // documentation for the SQL pattern in case a future feature needs
    // the same join.

    // ---- Reader helpers ----------------------------------------------------

    private static string? SafeString(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }
    private static bool? SafeBool(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetBoolean(i);
    }
    private static long? SafeLong(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return null;
        // seed_value / increment_value come back as sql_variant; reader surfaces them as long via CAST in SQL.
        return r.GetInt64(i);
    }
}
