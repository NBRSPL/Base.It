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
    /// Lists every user-authored object whose type is one we can sync:
    /// procedures, functions (scalar / inline / tvf), views, tables, and
    /// triggers. Filtered to <c>is_ms_shipped = 0</c> so system objects
    /// don't pollute the result.
    /// </summary>
    private const string ListAllQuery = NonBlockingPreamble + @"
SELECT SCHEMA_NAME(o.schema_id) AS schema_name, o.name, o.type
FROM sys.objects o
WHERE o.is_ms_shipped = 0
  AND o.type IN ('U','V','P','FN','TF','IF','TR')
ORDER BY schema_name, o.name";

    public async Task<SqlObjectType> GetObjectTypeAsync(
        string connectionString, ObjectIdentifier id, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(TypeQuery, conn);
        cmd.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;

        var result = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(result)) return SqlObjectType.Unknown;

        return result.Trim().ToUpperInvariant() switch
        {
            "U"  => SqlObjectType.Table,
            "V"  => SqlObjectType.View,
            "P"  => SqlObjectType.StoredProcedure,
            "FN" => SqlObjectType.ScalarFunction,
            "IF" => SqlObjectType.InlineTableFunction,
            "TF" => SqlObjectType.TableValuedFunction,
            "TR" => SqlObjectType.Trigger,
            _    => SqlObjectType.Unknown
        };
    }

    public async Task<SqlObject?> GetObjectAsync(
        string connectionString, ObjectIdentifier id, CancellationToken ct = default)
    {
        // Default fetch path: lightweight column-only table script, used
        // for drift detection / compare / ad-hoc fetching. Callers that
        // specifically need a DACPAC-ready definition should go through
        // GetObjectForDacpacAsync instead.
        var type = await GetObjectTypeAsync(connectionString, id, ct);
        if (type == SqlObjectType.Unknown) return null;

        string definition = type == SqlObjectType.Table
            ? await ScriptTableSimpleAsync(connectionString, id, ct)
            : await GetModuleDefinitionAsync(connectionString, id, ct);

        if (string.IsNullOrWhiteSpace(definition)) return null;
        return new SqlObject(id, type, definition, DefinitionHasher.Hash(definition));
    }

    public async Task<SqlObject?> GetObjectForDacpacAsync(
        string connectionString, ObjectIdentifier id, CancellationToken ct = default)
    {
        var type = await GetObjectTypeAsync(connectionString, id, ct);
        if (type == SqlObjectType.Unknown) return null;

        string definition = type == SqlObjectType.Table
            ? await ScriptTableForDacpacAsync(connectionString, id, ct)
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
                "U"  => SqlObjectType.Table,
                "V"  => SqlObjectType.View,
                "P"  => SqlObjectType.StoredProcedure,
                "FN" => SqlObjectType.ScalarFunction,
                "IF" => SqlObjectType.InlineTableFunction,
                "TF" => SqlObjectType.TableValuedFunction,
                "TR" => SqlObjectType.Trigger,
                _    => SqlObjectType.Unknown
            };
            if (sqlType == SqlObjectType.Unknown) continue;
            results.Add(new SqlObjectRef(new ObjectIdentifier(schema, name), sqlType));
        }
        return results;
    }

    /// <summary>
    /// Lightweight column-only CREATE TABLE. Used by the default fetch
    /// path (drift detection / Compare / Query fetch). Matches the
    /// pre-DACPAC output shape so existing hashes stay valid.
    /// </summary>
    private static async Task<string> ScriptTableSimpleAsync(
        string connectionString, ObjectIdentifier id, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(TableColumnsQuerySimple, conn);
        cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;

        var columns = new List<string>();
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
            columns.Add($"    [{name}] {typeSpec} {(isNullable ? "NULL" : "NOT NULL")}");
        }
        if (columns.Count == 0) return string.Empty;
        return $"CREATE TABLE [{id.Schema}].[{id.Name}] (\n{string.Join(",\n", columns)}\n);\n";
    }

    /// <summary>
    /// Produces a DACPAC/SSDT-shaped definition for a table: a full
    /// <c>CREATE TABLE</c> with columns, identity, defaults, computed
    /// columns, inline PK/UQ/CHECK constraints, followed by <c>CREATE
    /// INDEX</c> for every non-PK/UQ index, <c>ALTER TABLE ADD CONSTRAINT</c>
    /// for every foreign key, and a trailing <c>CREATE TRIGGER</c> block
    /// for each trigger bound to the table. Each top-level statement is
    /// separated by a <c>GO</c> batch terminator so the file can be
    /// executed directly against SQL Server.
    /// </summary>
    private static async Task<string> ScriptTableForDacpacAsync(
        string connectionString, ObjectIdentifier id, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Header (real casing + filegroup) and DB collation are read up
        // front so the body rendering can use them.
        var header     = await LoadTableHeaderAsync(conn, id, ct);
        if (header is null) return string.Empty;
        var dbCollation = await LoadDatabaseCollationAsync(conn, ct);

        // All remaining catalog queries run sequentially on one connection
        // so nothing interleaves mid-script. They're read-only + non-blocking.
        var columns   = await LoadColumnsAsync(conn, id, ct);
        if (columns.Count == 0) return string.Empty;
        var keyCons   = await LoadKeyConstraintsAsync(conn, id, ct);
        var checkCons = await LoadCheckConstraintsAsync(conn, id, ct);
        var fkeys     = await LoadForeignKeysAsync(conn, id, ct);
        var indexes   = await LoadIndexesAsync(conn, id, ct);
        var triggers  = await LoadTriggersAsync(conn, id, ct);

        // Use the real catalog name in the header so case matches the
        // database rather than whatever was typed into the Watch group.
        var realId = new ObjectIdentifier(header.Value.Schema, header.Value.Name);

        // Column alignment — SSDT-style: pad [Name] and type-spec columns
        // to their max width so everything after lines up cleanly.
        var nameField = columns.Select(c => $"[{c.Name}]").ToList();
        var typeField = columns.Select(c => RenderTypeSpec(c)).ToList();
        var maxName   = nameField.Max(s => s.Length);
        var maxType   = typeField.Max(s => s.Length);

        var sb = new System.Text.StringBuilder(capacity: 1024);

        // --- CREATE TABLE body: columns + inline PK/UQ/CHECK lines. -----
        sb.Append("CREATE TABLE [").Append(realId.Schema).Append("].[").Append(realId.Name).Append("] (\n");
        var bodyLines = new List<string>(columns.Count + keyCons.Count + checkCons.Count);
        for (int i = 0; i < columns.Count; i++)
            bodyLines.Add(RenderColumn(columns[i], nameField[i], typeField[i], maxName, maxType, dbCollation));
        foreach (var k in keyCons)   bodyLines.Add(RenderKeyConstraint(k));
        foreach (var c in checkCons) bodyLines.Add(RenderCheckConstraint(c));
        sb.Append(string.Join(",\n", bodyLines));
        sb.Append("\n)");
        if (!string.Equals(header.Value.Filegroup, "PRIMARY", StringComparison.OrdinalIgnoreCase))
            sb.Append(" ON [").Append(header.Value.Filegroup).Append(']');
        sb.Append(";\nGO\n");

        // --- Non-PK/UQ indexes as CREATE INDEX. -------------------------
        foreach (var ix in indexes)
            sb.Append(RenderIndex(ix, realId)).Append("GO\n");

        // --- Foreign keys as ALTER TABLE ADD CONSTRAINT. ----------------
        foreach (var fk in fkeys)
            sb.Append(RenderForeignKey(fk, realId)).Append("GO\n");

        // --- Triggers on this table, verbatim from sys.sql_modules. -----
        foreach (var (_, _, definition) in triggers)
        {
            sb.Append(definition.TrimEnd());
            sb.Append("\nGO\n");
        }

        return sb.ToString();
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

    private sealed record ColumnInfo(
        string Name,
        string TypeName,
        int    MaxLength,
        byte   Precision,
        byte   Scale,
        bool   IsNullable,
        bool   IsIdentity,
        long?  IdentitySeed,
        long?  IdentityIncrement,
        bool   IdentityNotForReplication,
        string? ComputedDefinition,
        bool?  ComputedIsPersisted,
        string? DefaultName,
        string? DefaultDefinition,
        string? CollationName,
        bool   IsRowGuidCol);

    private static async Task<List<ColumnInfo>> LoadColumnsAsync(
        SqlConnection conn, ObjectIdentifier id, CancellationToken ct)
    {
        var list = new List<ColumnInfo>();
        await using var cmd = new SqlCommand(TableColumnsQuery, conn);
        cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ColumnInfo(
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

    /// <summary>
    /// Renders one column line using SSDT/DACPAC conventions: uppercase
    /// type names, aligned <c>[Name]</c> and type-spec columns, <c>COLLATE</c>
    /// omitted when it matches the database default, inline <c>IDENTITY</c>
    /// / <c>ROWGUIDCOL</c> / <c>DEFAULT</c>, and a trailing <c>NULL</c> /
    /// <c>NOT NULL</c>.
    /// </summary>
    private static string RenderColumn(
        ColumnInfo c, string nameField, string typeField, int maxName, int maxType, string? dbCollation)
    {
        // Computed columns have no type / nullability / default — just the expression.
        if (c.ComputedDefinition is not null)
        {
            var persisted = c.ComputedIsPersisted == true ? " PERSISTED" : "";
            return $"    {nameField.PadRight(maxName)} AS {c.ComputedDefinition}{persisted}";
        }

        var sb = new System.Text.StringBuilder(capacity: 128);
        sb.Append("    ").Append(nameField.PadRight(maxName)).Append(' ')
          .Append(typeField.PadRight(maxType));

        // COLLATE only if non-null AND different from the database default.
        if (IsStringLikeType(c.TypeName)
            && !string.IsNullOrEmpty(c.CollationName)
            && !string.Equals(c.CollationName, dbCollation, StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" COLLATE ").Append(c.CollationName);
        }

        if (c.IsIdentity)
        {
            sb.Append(" IDENTITY(").Append(c.IdentitySeed ?? 1).Append(',').Append(c.IdentityIncrement ?? 1).Append(')');
            if (c.IdentityNotForReplication) sb.Append(" NOT FOR REPLICATION");
        }

        if (c.IsRowGuidCol)
            sb.Append(" ROWGUIDCOL");

        if (c.DefaultDefinition is not null)
        {
            sb.Append(' ');
            if (c.DefaultName is not null)
                sb.Append("CONSTRAINT [").Append(c.DefaultName).Append("] ");
            sb.Append("DEFAULT ").Append(c.DefaultDefinition);
        }

        sb.Append(c.IsNullable ? " NULL" : " NOT NULL");
        return sb.ToString();
    }

    /// <summary>
    /// Type spec in SSDT form: uppercase type name, space before the paren
    /// for char / binary / scale types (<c>NVARCHAR (65)</c>, <c>DATETIME2 (0)</c>)
    /// and no space for numeric types (<c>DECIMAL(6,2)</c>).
    /// </summary>
    private static string RenderTypeSpec(ColumnInfo c)
    {
        var upper = c.TypeName.ToUpperInvariant();
        var lower = c.TypeName.ToLowerInvariant();
        return lower switch
        {
            "char" or "varchar" or "binary" or "varbinary"
                => $"{upper} ({(c.MaxLength == -1 ? "MAX" : c.MaxLength.ToString())})",
            "nchar" or "nvarchar"
                => $"{upper} ({(c.MaxLength == -1 ? "MAX" : (c.MaxLength / 2).ToString())})",
            "decimal" or "numeric"
                => $"{upper}({c.Precision},{c.Scale})",
            "datetime2" or "datetimeoffset" or "time"
                => $"{upper} ({c.Scale})",
            _ => upper
        };
    }

    private static bool IsStringLikeType(string t) => t.ToLowerInvariant() is
        "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext";

    // ---- PK / UQ constraints ----------------------------------------------

    private sealed record KeyConstraintColumn(
        string ConstraintName,
        string ConstraintType,   // "PK" or "UQ"
        string IndexType,        // CLUSTERED / NONCLUSTERED
        byte   FillFactor,
        bool   IsPadded,
        string DataSpaceName,
        string ColumnName,
        bool   IsDescending);

    private static async Task<List<KeyConstraintGroup>> LoadKeyConstraintsAsync(
        SqlConnection conn, ObjectIdentifier id, CancellationToken ct)
    {
        var rows = new List<KeyConstraintColumn>();
        await using var cmd = new SqlCommand(TableKeyConstraintsQuery, conn);
        cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new KeyConstraintColumn(
                ConstraintName: reader.GetString(reader.GetOrdinal("constraint_name")),
                ConstraintType: reader.GetString(reader.GetOrdinal("constraint_type")).Trim(),
                IndexType:      reader.GetString(reader.GetOrdinal("index_type")),
                FillFactor:     reader.GetByte  (reader.GetOrdinal("fill_factor")),
                IsPadded:       reader.GetBoolean(reader.GetOrdinal("is_padded")),
                DataSpaceName:  reader.GetString(reader.GetOrdinal("data_space_name")),
                ColumnName:     reader.GetString(reader.GetOrdinal("column_name")),
                IsDescending:   reader.GetBoolean(reader.GetOrdinal("is_descending_key"))));
        }
        return rows.GroupBy(r => r.ConstraintName)
                   .Select(g => new KeyConstraintGroup(
                       Name:          g.Key,
                       Type:          g.First().ConstraintType,
                       IndexType:     g.First().IndexType,
                       FillFactor:    g.First().FillFactor,
                       IsPadded:      g.First().IsPadded,
                       DataSpaceName: g.First().DataSpaceName,
                       Columns:       g.Select(r => (r.ColumnName, r.IsDescending)).ToList()))
                   .ToList();
    }

    private sealed record KeyConstraintGroup(
        string Name, string Type, string IndexType,
        byte FillFactor, bool IsPadded, string DataSpaceName,
        List<(string Column, bool Desc)> Columns);

    private static string RenderKeyConstraint(KeyConstraintGroup k)
    {
        var kind = k.Type.Equals("PK", StringComparison.OrdinalIgnoreCase)
            ? "PRIMARY KEY"
            : "UNIQUE";
        var cols = string.Join(", ", k.Columns.Select(c => $"[{c.Column}] {(c.Desc ? "DESC" : "ASC")}"));
        var with = new List<string>();
        if (k.FillFactor > 0) with.Add($"FILLFACTOR = {k.FillFactor}");
        if (k.IsPadded)       with.Add("PAD_INDEX = ON");
        var withClause = with.Count == 0 ? "" : $" WITH ({string.Join(", ", with)})";
        var onClause   = k.DataSpaceName.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $" ON [{k.DataSpaceName}]";
        return $"    CONSTRAINT [{k.Name}] {kind} {k.IndexType} ({cols}){withClause}{onClause}";
    }

    // ---- Check constraints -------------------------------------------------

    private sealed record CheckConstraintInfo(
        string Name, string Definition, bool IsNotTrusted, bool IsNotForReplication);

    private static async Task<List<CheckConstraintInfo>> LoadCheckConstraintsAsync(
        SqlConnection conn, ObjectIdentifier id, CancellationToken ct)
    {
        var list = new List<CheckConstraintInfo>();
        await using var cmd = new SqlCommand(TableCheckConstraintsQuery, conn);
        cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new CheckConstraintInfo(
                Name:                reader.GetString(0),
                Definition:          reader.GetString(1),
                IsNotTrusted:        reader.GetBoolean(2),
                IsNotForReplication: reader.GetBoolean(3)));
        }
        return list;
    }

    /// <summary>
    /// Renders one named CHECK constraint. <c>NOT FOR REPLICATION</c>
    /// goes between <c>CHECK</c> and the predicate per T-SQL grammar —
    /// dropping it would let replication agents trigger checks they
    /// were configured to skip on production.
    /// </summary>
    private static string RenderCheckConstraint(CheckConstraintInfo c)
    {
        var nfr = c.IsNotForReplication ? " NOT FOR REPLICATION" : "";
        return $"    CONSTRAINT [{c.Name}] CHECK{nfr} {c.Definition}";
    }

    // ---- Foreign keys ------------------------------------------------------

    private sealed record ForeignKeyColumn(
        string ConstraintName, bool IsNotTrusted, bool IsNotForReplication,
        string RefSchema, string RefTable,
        string ColumnName, string RefColumn, string OnDelete, string OnUpdate);

    private static async Task<List<ForeignKeyGroup>> LoadForeignKeysAsync(
        SqlConnection conn, ObjectIdentifier id, CancellationToken ct)
    {
        var rows = new List<ForeignKeyColumn>();
        await using var cmd = new SqlCommand(TableForeignKeysQuery, conn);
        cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ForeignKeyColumn(
                ConstraintName:      reader.GetString(0),
                IsNotTrusted:        reader.GetBoolean(1),
                IsNotForReplication: reader.GetBoolean(2),
                RefSchema:           reader.GetString(3),
                RefTable:            reader.GetString(4),
                ColumnName:          reader.GetString(6),
                RefColumn:           reader.GetString(7),
                OnDelete:            reader.GetString(8),
                OnUpdate:            reader.GetString(9)));
        }
        return rows.GroupBy(r => r.ConstraintName)
                   .Select(g => new ForeignKeyGroup(
                       Name:                g.Key,
                       IsNotTrusted:        g.First().IsNotTrusted,
                       IsNotForReplication: g.First().IsNotForReplication,
                       RefSchema:           g.First().RefSchema,
                       RefTable:            g.First().RefTable,
                       OnDelete:            g.First().OnDelete,
                       OnUpdate:            g.First().OnUpdate,
                       Columns:             g.Select(r => (r.ColumnName, r.RefColumn)).ToList()))
                   .ToList();
    }

    private sealed record ForeignKeyGroup(
        string Name, bool IsNotTrusted, bool IsNotForReplication,
        string RefSchema, string RefTable,
        string OnDelete, string OnUpdate,
        List<(string Column, string RefColumn)> Columns);

    /// <summary>
    /// Emits a foreign-key as <c>ALTER TABLE ... ADD CONSTRAINT</c>.
    /// <c>NOT FOR REPLICATION</c> sits after the column list and before
    /// the optional <c>ON DELETE</c>/<c>ON UPDATE</c> clauses per T-SQL
    /// grammar.
    /// </summary>
    private static string RenderForeignKey(ForeignKeyGroup fk, ObjectIdentifier id)
    {
        var cols    = string.Join(", ", fk.Columns.Select(c => $"[{c.Column}]"));
        var refCols = string.Join(", ", fk.Columns.Select(c => $"[{c.RefColumn}]"));
        var check   = fk.IsNotTrusted ? "WITH NOCHECK" : "WITH CHECK";
        var nfr     = fk.IsNotForReplication ? " NOT FOR REPLICATION" : "";
        var onDel   = fk.OnDelete.Equals("NO_ACTION", StringComparison.OrdinalIgnoreCase)
            ? "" : $" ON DELETE {fk.OnDelete.Replace('_', ' ')}";
        var onUpd   = fk.OnUpdate.Equals("NO_ACTION", StringComparison.OrdinalIgnoreCase)
            ? "" : $" ON UPDATE {fk.OnUpdate.Replace('_', ' ')}";
        return $"ALTER TABLE [{id.Schema}].[{id.Name}] {check} ADD CONSTRAINT [{fk.Name}] " +
               $"FOREIGN KEY ({cols}) REFERENCES [{fk.RefSchema}].[{fk.RefTable}] ({refCols})" +
               $"{nfr}{onDel}{onUpd};\n";
    }

    // ---- Non-PK/UQ indexes -------------------------------------------------

    private sealed record IndexColumn(
        string IndexName, string TypeDesc, bool IsUnique, string? Filter,
        bool IsIncluded, string ColumnName, bool IsDescending);

    private static async Task<List<IndexGroup>> LoadIndexesAsync(
        SqlConnection conn, ObjectIdentifier id, CancellationToken ct)
    {
        var rows = new List<IndexColumn>();
        await using var cmd = new SqlCommand(TableIndexesQuery, conn);
        cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new IndexColumn(
                IndexName:    reader.GetString(0),
                TypeDesc:     reader.GetString(1),
                IsUnique:     reader.GetBoolean(2),
                Filter:       SafeString(reader, "filter_definition"),
                IsIncluded:   reader.GetBoolean(6),
                ColumnName:   reader.GetString(7),
                IsDescending: reader.GetBoolean(8)));
        }
        return rows.GroupBy(r => r.IndexName)
                   .Select(g => new IndexGroup(
                       Name:     g.Key,
                       TypeDesc: g.First().TypeDesc,
                       IsUnique: g.First().IsUnique,
                       Filter:   g.First().Filter,
                       KeyCols:     g.Where(r => !r.IsIncluded)
                                     .Select(r => (r.ColumnName, r.IsDescending)).ToList(),
                       IncludeCols: g.Where(r => r.IsIncluded)
                                     .Select(r => r.ColumnName).ToList()))
                   .ToList();
    }

    private sealed record IndexGroup(
        string Name, string TypeDesc, bool IsUnique, string? Filter,
        List<(string Column, bool Desc)> KeyCols,
        List<string> IncludeCols);

    private static string RenderIndex(IndexGroup ix, ObjectIdentifier id)
    {
        var unique  = ix.IsUnique ? "UNIQUE " : "";
        var keyCols = string.Join(", ", ix.KeyCols.Select(c => $"[{c.Column}] {(c.Desc ? "DESC" : "ASC")}"));
        var include = ix.IncludeCols.Count == 0
            ? ""
            : $" INCLUDE ({string.Join(", ", ix.IncludeCols.Select(c => $"[{c}]"))})";
        var filter  = string.IsNullOrWhiteSpace(ix.Filter) ? "" : $" WHERE {ix.Filter}";
        return $"CREATE {unique}{ix.TypeDesc} INDEX [{ix.Name}] " +
               $"ON [{id.Schema}].[{id.Name}] ({keyCols}){include}{filter};\n";
    }

    // ---- Triggers on this table -------------------------------------------

    private static async Task<List<(string Schema, string Name, string Definition)>> LoadTriggersAsync(
        SqlConnection conn, ObjectIdentifier id, CancellationToken ct)
    {
        var list = new List<(string, string, string)>();
        await using var cmd = new SqlCommand(TableTriggersQuery, conn);
        cmd.Parameters.Add("@name",   System.Data.SqlDbType.NVarChar, 128).Value = id.Name;
        cmd.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = id.Schema;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add((reader.GetString(1), reader.GetString(0), reader.GetString(2)));
        return list;
    }

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
