using System.IO.Compression;
using System.Text;
using Base.It.Core.Hashing;
using Base.It.Core.Models;
using Base.It.Core.Sql;
using Microsoft.Data.SqlClient;

namespace Base.It.Core.Schema;

/// <summary>
/// Bulk-fetches every user-authored object's full definition from a SQL
/// Server. Three modes:
/// <list type="bullet">
///   <item><b>FetchAllAsync</b> — modules + tables in <see cref="ParallelPartitions"/>
///         parallel partitions. Each partition runs on its own SqlConnection
///         with a large TDS packet size so latency-bound WAN links aren't
///         throttled by 4 KB packet round-trips.</item>
///   <item><b>FetchMetadataAsync</b> — lightweight (no definitions) catalog
///         dump used by the incremental snapshot path to decide what
///         actually changed.</item>
///   <item><b>FetchByObjectIdsAsync</b> — fetches definitions for a specific
///         set of object IDs. Batched at 1,000 IDs per query.</item>
/// </list>
///
/// Network optimisations:
/// <list type="bullet">
///   <item><b>PacketSize=16384</b> — 4× the default 4 KB TDS packet. On a
///         high-latency link (corporate VPN, cross-region) each packet
///         costs an RTT; bigger packets directly reduce that overhead.</item>
///   <item><b>COMPRESS()</b> server-side on the definition column — typical
///         SQL text gzips to ~20% of original. ~5× less bytes on the wire.
///         Probed once per call; falls back transparently on pre-2016 SQL
///         Server.</item>
///   <item><b>N-way partition</b> on <c>object_id % N</c> — each partition
///         runs on its own connection in parallel. Multiplies throughput
///         on latency-bound links without piling load on the server (each
///         partition reads a disjoint slice of <c>sys.objects</c>).</item>
/// </list>
/// </summary>
public sealed class BulkSchemaFetcher
{
    // Read-committed + lock_timeout = 0 so a long-running app's locks
    // don't block our catalog read.
    private const string NonBlockingPreamble =
        "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;\n" +
        "SET LOCK_TIMEOUT 0;\n";

    private const string ModuleTypeFilter =
        "AND o.type IN ('P','FN','IF','TF','V','TR','FS','FT','PC','AF','RF')";

    private const string AllUserObjectTypesFilter =
        "AND o.type IN ('U','P','FN','IF','TF','V','TR','FS','FT','PC','AF','RF')";

    /// <summary>
    /// How many parallel connections to use for the bulk fetch. Picked
    /// to multiply throughput on latency-bound links without piling load
    /// on the server. Each connection runs against a disjoint slice of
    /// <c>sys.objects</c> via <c>object_id % N = part</c>.
    /// </summary>
    private const int ParallelPartitions = 4;

    /// <summary>
    /// TDS packet size for fetch connections — 4× the SqlClient default.
    /// On a high-latency WAN every packet costs an RTT, so bigger
    /// packets translate directly into less wall-clock for the same
    /// payload. SqlClient supports up to 32 KB but 16 KB is the
    /// safe-everywhere sweet spot.
    /// </summary>
    private const int FetchPacketSize = 16384;

    /// <summary>Lightweight (no definitions) per-object metadata for incremental diffs.</summary>
    public sealed record ObjectMetadata(
        int ObjectId,
        string Schema,
        string Name,
        SqlObjectType Kind,
        DateTime ModifyDateUtc);

    /// <summary>
    /// Trigger → parent table mapping. Used by the snapshotter to attach
    /// <c>ParentSchema</c> / <c>ParentName</c> to every trigger entry so
    /// the table-preview UI can show a "Triggers on this table" list
    /// without having to grep the CREATE TRIGGER text for the ON clause.
    /// One row per trigger. Cheap — single catalog query, no compression.
    /// </summary>
    public async Task<Dictionary<string, (string Schema, string Name)>> FetchTriggerParentsAsync(
        string connectionString, CancellationToken ct = default)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(connectionString)) return map;

        const string Sql = NonBlockingPreamble + @"
SELECT
    SCHEMA_NAME(t.schema_id)   AS trigger_schema,
    tr.name                    AS trigger_name,
    t.name                     AS parent_table
FROM sys.triggers tr
INNER JOIN sys.tables t ON t.object_id = tr.parent_id
WHERE tr.is_ms_shipped = 0";

        await using var conn = new SqlConnection(WithFastPacketSize(connectionString));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(Sql, conn) { CommandTimeout = 60 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var trigSchema = reader.GetString(0);
            var trigName   = reader.GetString(1);
            var parentName = reader.GetString(2);
            // Trigger schema = parent table's schema (sys.triggers has
            // no schema_id of its own), so the trigger key is just
            // {trigSchema}.{trigName}.
            var key = $"{trigSchema.ToUpperInvariant()}.{trigName.ToUpperInvariant()}";
            map[key] = (trigSchema, parentName);
        }
        return map;
    }

    /// <summary>Tagged result of <see cref="FetchAllAsync"/>.</summary>
    public sealed record FetchResult(
        IReadOnlyList<SqlObject> Objects,
        bool UsedCompression,
        int Connections);

    // ─────────────────── Full bulk fetch (no previous) ───────────────────

    public async Task<FetchResult> FetchAllAsync(
        string connectionString,
        IProgress<int>? rowProgress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return new FetchResult(Array.Empty<SqlObject>(), false, 0);

        int fetched = 0;
        void Bump()
        {
            var n = Interlocked.Increment(ref fetched);
            if (n == 1 || n % 100 == 0) rowProgress?.Report(n);
        }

        var fastConn = WithFastPacketSize(connectionString);

        // Probe COMPRESS once; share the result with every parallel fetch.
        bool useCompression;
        await using (var probe = new SqlConnection(fastConn))
        {
            await probe.OpenAsync(ct);
            useCompression = await ServerSupportsCompressAsync(probe, ct);
        }

        // Fire off all partition queries in parallel. N partitions for
        // modules + 1 for tables = N+1 simultaneous connections.
        var moduleTasks = new List<Task<List<SqlObject>>>(ParallelPartitions);
        for (int part = 0; part < ParallelPartitions; part++)
        {
            int p = part;
            moduleTasks.Add(FetchModulesPartitionAsync(
                fastConn, useCompression, p, ParallelPartitions, Bump, ct));
        }
        var tablesTask = FetchTablesAsync(fastConn, Bump, ct);

        await Task.WhenAll(moduleTasks.Concat(new[] { Task.WhenAny(tablesTask).Unwrap() }));
        await Task.WhenAll(moduleTasks); // ensure all completed
        await tablesTask;

        var result = new List<SqlObject>();
        foreach (var t in moduleTasks) result.AddRange(t.Result);
        result.AddRange(tablesTask.Result);

        rowProgress?.Report(result.Count);
        return new FetchResult(result, useCompression, ParallelPartitions + 1);
    }

    // ─────────────────── Metadata-only (incremental path) ───────────────────

    public async Task<IReadOnlyList<ObjectMetadata>> FetchMetadataAsync(
        string connectionString,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return Array.Empty<ObjectMetadata>();

        const string Sql = NonBlockingPreamble + @"
SELECT
    o.object_id,
    SCHEMA_NAME(o.schema_id) AS schema_name,
    o.name                   AS object_name,
    o.type                   AS type_code,
    o.modify_date            AS modify_date
FROM sys.objects o
WHERE o.is_ms_shipped = 0
" + AllUserObjectTypesFilter;

        var list = new List<ObjectMetadata>(8000);
        await using var conn = new SqlConnection(WithFastPacketSize(connectionString));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(Sql, conn) { CommandTimeout = 120 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var oid    = reader.GetInt32(0);
            var schema = reader.GetString(1);
            var name   = reader.GetString(2);
            var code   = reader.GetString(3).Trim().ToUpperInvariant();
            var modify = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc);

            var kind = TypeCodeToKind(code);
            if (kind == SqlObjectType.Unknown) continue;

            list.Add(new ObjectMetadata(oid, schema, name, kind, modify));
        }
        return list;
    }

    /// <summary>
    /// "What changed since X?" — returns every user object whose
    /// <c>sys.objects.modify_date</c> is at or after <paramref name="sinceUtc"/>.
    /// Powers the Snapshots "Recent changes" panel: pick a date, hit
    /// Refresh, see the procs / views / tables / triggers / functions
    /// touched since then, ticked rows hand off to Batch via the
    /// existing SendToBatchPayload route. Server-side ORDER BY puts the
    /// freshest changes first.
    ///
    /// Caveats: <c>modify_date</c> tracks definition changes (ALTER,
    /// DROP+CREATE, schema), not data changes (INSERT / UPDATE / DELETE
    /// don't bump it). Triggers have their own modify_date — changing
    /// a trigger doesn't bump its parent table's. SSMS-style design
    /// changes that drop+recreate reset <c>create_date</c> too. So
    /// this is a useful "what's been touched lately" signal, not an
    /// audit log.
    /// </summary>
    public async Task<IReadOnlyList<ObjectMetadata>> FetchChangedSinceAsync(
        string connectionString,
        DateTime sinceUtc,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return Array.Empty<ObjectMetadata>();

        const string Sql = NonBlockingPreamble + @"
SELECT
    o.object_id,
    SCHEMA_NAME(o.schema_id) AS schema_name,
    o.name                   AS object_name,
    o.type                   AS type_code,
    o.modify_date            AS modify_date
FROM sys.objects o
WHERE o.is_ms_shipped = 0
  AND o.modify_date >= @since
" + AllUserObjectTypesFilter + @"
ORDER BY o.modify_date DESC, schema_name, o.name";

        var list = new List<ObjectMetadata>(64);
        await using var conn = new SqlConnection(WithFastPacketSize(connectionString));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(Sql, conn) { CommandTimeout = 60 };
        cmd.Parameters.Add("@since", System.Data.SqlDbType.DateTime2).Value =
            DateTime.SpecifyKind(sinceUtc, DateTimeKind.Unspecified);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var oid    = reader.GetInt32(0);
            var schema = reader.GetString(1);
            var name   = reader.GetString(2);
            var code   = reader.GetString(3).Trim().ToUpperInvariant();
            var modify = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc);

            var kind = TypeCodeToKind(code);
            if (kind == SqlObjectType.Unknown) continue;

            list.Add(new ObjectMetadata(oid, schema, name, kind, modify));
        }
        return list;
    }

    public async Task<FetchResult> FetchByObjectIdsAsync(
        string connectionString,
        IReadOnlyList<int> objectIds,
        IProgress<int>? rowProgress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || objectIds.Count == 0)
            return new FetchResult(Array.Empty<SqlObject>(), false, 0);

        var fastConn = WithFastPacketSize(connectionString);

        bool useCompression;
        await using (var probe = new SqlConnection(fastConn))
        {
            await probe.OpenAsync(ct);
            useCompression = await ServerSupportsCompressAsync(probe, ct);
        }

        var results = new List<SqlObject>(objectIds.Count);
        int fetched = 0;
        void Bump()
        {
            var n = Interlocked.Increment(ref fetched);
            if (n == 1 || n % 50 == 0) rowProgress?.Report(n);
        }

        const int BatchSize = 1000;
        for (int offset = 0; offset < objectIds.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = objectIds.Skip(offset).Take(BatchSize).ToList();

            // Modules + tables for this batch run in parallel — even
            // small change sets benefit from not serialising the two.
            var modulesTask = FetchModulesByIdsAsync(fastConn, batch, useCompression, Bump, ct);
            var tablesTask  = FetchTablesByIdsAsync (fastConn, batch,                  Bump, ct);
            await Task.WhenAll(modulesTask, tablesTask);

            results.AddRange(modulesTask.Result);
            results.AddRange(tablesTask.Result);
        }

        rowProgress?.Report(results.Count);
        return new FetchResult(results, useCompression, 2);
    }

    // ─────────────────── Modules: full (partitioned) and by-id ───────────────────

    private static async Task<List<SqlObject>> FetchModulesPartitionAsync(
        string connectionString, bool useCompression,
        int partition, int totalPartitions,
        Action onRow, CancellationToken ct)
    {
        var defExpr = useCompression
            ? "COMPRESS(CONVERT(VARBINARY(MAX), sm.definition))"
            : "sm.definition";

        var query = NonBlockingPreamble + $@"
SELECT SCHEMA_NAME(o.schema_id), o.name, o.type, {defExpr}
FROM sys.objects o
INNER JOIN sys.sql_modules sm ON sm.object_id = o.object_id
WHERE o.is_ms_shipped = 0
  AND (o.object_id % @parts) = @part
  {ModuleTypeFilter}";

        var parameters = new List<SqlParameter>
        {
            new SqlParameter("@parts", System.Data.SqlDbType.Int) { Value = totalPartitions },
            new SqlParameter("@part",  System.Data.SqlDbType.Int) { Value = partition },
        };

        return await ReadModulesAsync(connectionString, query, parameters, useCompression, onRow, ct);
    }

    private static async Task<List<SqlObject>> FetchModulesByIdsAsync(
        string connectionString, IReadOnlyList<int> ids, bool useCompression, Action onRow, CancellationToken ct)
    {
        var paramNames = new List<string>(ids.Count);
        var parameters = new List<SqlParameter>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            var p = $"@id{i}";
            paramNames.Add(p);
            parameters.Add(new SqlParameter(p, System.Data.SqlDbType.Int) { Value = ids[i] });
        }
        var inList = string.Join(",", paramNames);

        var defExpr = useCompression
            ? "COMPRESS(CONVERT(VARBINARY(MAX), sm.definition))"
            : "sm.definition";

        var query = NonBlockingPreamble +
            $@"SELECT SCHEMA_NAME(o.schema_id), o.name, o.type, {defExpr}
               FROM sys.objects o INNER JOIN sys.sql_modules sm ON sm.object_id = o.object_id
               WHERE o.object_id IN ({inList})
                 {ModuleTypeFilter}";

        return await ReadModulesAsync(connectionString, query, parameters, useCompression, onRow, ct);
    }

    private static async Task<List<SqlObject>> ReadModulesAsync(
        string connectionString, string query, IReadOnlyList<SqlParameter>? parameters,
        bool useCompression, Action onRow, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 600 };
        if (parameters is not null) foreach (var p in parameters) cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct);

        var results = new List<SqlObject>();
        while (await reader.ReadAsync(ct))
        {
            var schema     = reader.GetString(0);
            var name       = reader.GetString(1);
            var typeCode   = reader.GetString(2).Trim().ToUpperInvariant();

            string definition;
            if (useCompression)
            {
                if (await reader.IsDBNullAsync(3, ct)) continue;
                var compressed = (byte[])reader[3];
                definition = DecompressUtf16Le(compressed);
            }
            else
            {
                if (await reader.IsDBNullAsync(3, ct)) continue;
                definition = reader.GetString(3);
            }
            if (string.IsNullOrWhiteSpace(definition)) continue;

            var kind = TypeCodeToKind(typeCode);
            if (kind == SqlObjectType.Unknown) continue;

            results.Add(new SqlObject(
                new ObjectIdentifier(schema, name),
                kind,
                definition,
                DefinitionHasher.Hash(definition)));
            onRow();
        }
        return results;
    }

    // ─────────────────── Tables: full and by-id ───────────────────
    //
    // The bulk-table fetch runs 6 server-side queries: db collation,
    // headers, columns (rich), key constraints, check constraints,
    // foreign keys, and indexes. They scope to the same set of tables —
    // either every user table or a specific object-id list — and the
    // shared `TableScriptRenderer` then emits the final DACPAC-shaped
    // SQL per (schema, name). This way snapshots capture PK / UQ /
    // CHECK / FK / DEFAULT / IDENTITY / COMPUTED / INDEX, not just
    // columns. Triggers are deliberately fetched as their own
    // top-level objects (type 'TR' via the modules path) and not
    // embedded in the table SQL — keeps them addressable by themselves.

    private const string AllTableHeadersQuery = NonBlockingPreamble + @"
SELECT
    t.object_id,
    SCHEMA_NAME(t.schema_id) AS schema_name,
    t.name                    AS table_name,
    ds.name                   AS filegroup_name
FROM sys.tables t
INNER JOIN sys.indexes     i  ON i.object_id = t.object_id AND i.index_id IN (0, 1)
INNER JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
WHERE t.is_ms_shipped = 0
{0}";

    private const string AllTableColumnsRichQuery = NonBlockingPreamble + @"
SELECT
    t.object_id,
    SCHEMA_NAME(t.schema_id)          AS schema_name,
    t.name                            AS table_name,
    c.column_id,
    c.name                            AS column_name,
    ty.name                           AS type_name,
    c.max_length,
    c.precision,
    c.scale,
    c.is_nullable,
    c.is_identity,
    CAST(ic.seed_value      AS BIGINT) AS identity_seed,
    CAST(ic.increment_value AS BIGINT) AS identity_increment,
    ic.is_not_for_replication          AS identity_not_for_replication,
    cc.definition                      AS computed_definition,
    cc.is_persisted                    AS computed_is_persisted,
    dc.name                            AS default_name,
    dc.definition                      AS default_definition,
    c.collation_name,
    c.is_rowguidcol
FROM sys.tables t
INNER JOIN sys.columns c ON c.object_id = t.object_id
INNER JOIN sys.types   ty ON c.user_type_id = ty.user_type_id
LEFT JOIN sys.identity_columns   ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
LEFT JOIN sys.computed_columns   cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE t.is_ms_shipped = 0
{0}
ORDER BY t.object_id, c.column_id";

    private const string AllTableKeyConstraintsQuery = NonBlockingPreamble + @"
SELECT
    t.object_id,
    SCHEMA_NAME(t.schema_id)   AS schema_name,
    t.name                      AS table_name,
    kc.name                     AS constraint_name,
    kc.type                     AS constraint_type,
    i.type_desc                 AS index_type,
    i.fill_factor,
    i.is_padded,
    ds.name                     AS data_space_name,
    ic.key_ordinal,
    col.name                    AS column_name,
    ic.is_descending_key
FROM sys.key_constraints kc
INNER JOIN sys.tables    t   ON t.object_id = kc.parent_object_id
INNER JOIN sys.indexes   i   ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
INNER JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
INNER JOIN sys.columns   col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
WHERE t.is_ms_shipped = 0
{0}
ORDER BY t.object_id, kc.name, ic.key_ordinal";

    private const string AllTableCheckConstraintsQuery = NonBlockingPreamble + @"
SELECT
    t.object_id,
    cc.name,
    cc.definition,
    cc.is_not_trusted,
    cc.is_not_for_replication
FROM sys.check_constraints cc
INNER JOIN sys.tables t ON t.object_id = cc.parent_object_id
WHERE t.is_ms_shipped = 0
{0}
ORDER BY t.object_id, cc.name";

    private const string AllTableForeignKeysQuery = NonBlockingPreamble + @"
SELECT
    t.object_id,
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
WHERE t.is_ms_shipped = 0
{0}
ORDER BY t.object_id, fk.name, fkc.constraint_column_id";

    private const string AllTableIndexesQuery = NonBlockingPreamble + @"
SELECT
    t.object_id,
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
WHERE t.is_ms_shipped = 0
  AND i.is_primary_key = 0
  AND i.is_unique_constraint = 0
  AND i.type > 0
{0}
ORDER BY t.object_id, i.name, ic.is_included_column, ic.key_ordinal, ic.index_column_id";

    private const string DatabaseCollationQuery =
        "SELECT CONVERT(NVARCHAR(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))";

    private static async Task<List<SqlObject>> FetchTablesAsync(
        string connectionString, Action onRow, CancellationToken ct)
        => await ReadTablesAsync(connectionString,
            objectIdFilter: "",
            parameters: null,
            onRow, ct);

    private static async Task<List<SqlObject>> FetchTablesByIdsAsync(
        string connectionString, IReadOnlyList<int> ids, Action onRow, CancellationToken ct)
    {
        if (ids.Count == 0) return new List<SqlObject>();
        var paramNames = new List<string>(ids.Count);
        var parameters = new List<SqlParameter>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            var p = $"@id{i}";
            paramNames.Add(p);
            parameters.Add(new SqlParameter(p, System.Data.SqlDbType.Int) { Value = ids[i] });
        }
        var idFilter = $"AND t.object_id IN ({string.Join(",", paramNames)})";
        return await ReadTablesAsync(connectionString, idFilter, parameters, onRow, ct);
    }

    /// <summary>
    /// Runs the 6 catalog queries for the requested set of tables (all
    /// user tables when <paramref name="objectIdFilter"/> is empty,
    /// otherwise scoped via <c>AND t.object_id IN (...)</c>) and renders
    /// one constraint-aware <see cref="SqlObject"/> per table using
    /// <see cref="TableScriptRenderer"/>. Queries run sequentially on one
    /// connection so the catalog state is consistent; on a typical
    /// 7k-object database the total time is dominated by the columns
    /// query, the constraint queries are short.
    /// </summary>
    private static async Task<List<SqlObject>> ReadTablesAsync(
        string connectionString, string objectIdFilter,
        IReadOnlyList<SqlParameter>? parameters,
        Action onRow, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Single value, used for COLLATE elision in column rendering.
        string? dbCollation;
        {
            await using var cmd = new SqlCommand(DatabaseCollationQuery, conn);
            dbCollation = await cmd.ExecuteScalarAsync(ct) as string;
        }

        // Headers (one row per table). Skip if the table has no
        // heap/clustered row in sys.indexes — that means it doesn't
        // physically exist (rare; legacy graph tables).
        var headers = await ReadHeadersAsync(conn, objectIdFilter, parameters, ct);
        if (headers.Count == 0) return new List<SqlObject>();

        var columnsByOid  = await ReadAllColumnsAsync          (conn, objectIdFilter, parameters, ct);
        var keysByOid     = await ReadAllKeyConstraintsAsync   (conn, objectIdFilter, parameters, ct);
        var checksByOid   = await ReadAllCheckConstraintsAsync (conn, objectIdFilter, parameters, ct);
        var fkeysByOid    = await ReadAllForeignKeysAsync      (conn, objectIdFilter, parameters, ct);
        var indexesByOid  = await ReadAllIndexesAsync          (conn, objectIdFilter, parameters, ct);

        var results = new List<SqlObject>(headers.Count);
        foreach (var (oid, schema, name, filegroup) in headers)
        {
            if (!columnsByOid.TryGetValue(oid, out var columns) || columns.Count == 0) continue;
            keysByOid   .TryGetValue(oid, out var keys);
            checksByOid .TryGetValue(oid, out var checks);
            fkeysByOid  .TryGetValue(oid, out var fkeys);
            indexesByOid.TryGetValue(oid, out var indexes);

            var definition = TableScriptRenderer.Render(
                schema:           schema,
                name:             name,
                filegroup:        filegroup,
                columns:          columns,
                keyConstraints:   keys    ?? new List<TableScriptRenderer.KeyConstraintGroup>(),
                checkConstraints: checks  ?? new List<TableScriptRenderer.CheckConstraintInfo>(),
                foreignKeys:      fkeys   ?? new List<TableScriptRenderer.ForeignKeyGroup>(),
                indexes:          indexes ?? new List<TableScriptRenderer.IndexGroup>(),
                triggers:         Array.Empty<(string, string, string)>(),  // triggers fetched as type='TR'
                dbCollation:      dbCollation);
            if (string.IsNullOrWhiteSpace(definition)) continue;

            results.Add(new SqlObject(
                new ObjectIdentifier(schema, name),
                SqlObjectType.Table,
                definition,
                DefinitionHasher.Hash(definition)));
            onRow();
        }
        return results;
    }

    // ─── Per-aspect bulk readers ───────────────────────────────────────────
    //
    // Each one runs ONE query that returns rows for every (in-scope) user
    // table, then aggregates per object_id into the same record types
    // SqlObjectScripter uses for its per-table fetch. Results are
    // dictionary-keyed by object_id so the outer table loop is O(1).

    private static SqlCommand BuildScopedCommand(
        SqlConnection conn,
        string queryTemplate,
        string objectIdFilter,
        IReadOnlyList<SqlParameter>? parameters)
    {
        var cmd = new SqlCommand(string.Format(queryTemplate, objectIdFilter), conn)
        {
            CommandTimeout = 600
        };
        if (parameters is not null)
            foreach (var p in parameters)
                // SqlParameter can't be added to two commands — clone.
                cmd.Parameters.Add(new SqlParameter(p.ParameterName, p.SqlDbType) { Value = p.Value });
        return cmd;
    }

    private static async Task<List<(int Oid, string Schema, string Name, string Filegroup)>> ReadHeadersAsync(
        SqlConnection conn, string objectIdFilter, IReadOnlyList<SqlParameter>? parameters, CancellationToken ct)
    {
        var list = new List<(int, string, string, string)>();
        await using var cmd = BuildScopedCommand(conn, AllTableHeadersQuery, objectIdFilter, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return list;
    }

    private static async Task<Dictionary<int, List<TableScriptRenderer.ColumnInfo>>> ReadAllColumnsAsync(
        SqlConnection conn, string objectIdFilter, IReadOnlyList<SqlParameter>? parameters, CancellationToken ct)
    {
        var map = new Dictionary<int, List<TableScriptRenderer.ColumnInfo>>();
        await using var cmd = BuildScopedCommand(conn, AllTableColumnsRichQuery, objectIdFilter, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var oid = reader.GetInt32(reader.GetOrdinal("object_id"));
            if (!map.TryGetValue(oid, out var list))
            {
                list = new List<TableScriptRenderer.ColumnInfo>();
                map[oid] = list;
            }
            list.Add(new TableScriptRenderer.ColumnInfo(
                Name:                      reader.GetString (reader.GetOrdinal("column_name")),
                TypeName:                  reader.GetString (reader.GetOrdinal("type_name")),
                MaxLength:                 reader.GetInt16  (reader.GetOrdinal("max_length")),
                Precision:                 reader.GetByte   (reader.GetOrdinal("precision")),
                Scale:                     reader.GetByte   (reader.GetOrdinal("scale")),
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
        return map;
    }

    private static async Task<Dictionary<int, List<TableScriptRenderer.KeyConstraintGroup>>> ReadAllKeyConstraintsAsync(
        SqlConnection conn, string objectIdFilter, IReadOnlyList<SqlParameter>? parameters, CancellationToken ct)
    {
        // First pass: flat rows (one per constraint+column).
        var rows = new List<(int Oid, string Name, string Type, string IndexType, byte FillFactor,
                             bool IsPadded, string DataSpace, string Column, bool Desc)>();
        await using (var cmd = BuildScopedCommand(conn, AllTableKeyConstraintsQuery, objectIdFilter, parameters))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add((
                    reader.GetInt32 (reader.GetOrdinal("object_id")),
                    reader.GetString(reader.GetOrdinal("constraint_name")),
                    reader.GetString(reader.GetOrdinal("constraint_type")).Trim(),
                    reader.GetString(reader.GetOrdinal("index_type")),
                    reader.GetByte  (reader.GetOrdinal("fill_factor")),
                    reader.GetBoolean(reader.GetOrdinal("is_padded")),
                    reader.GetString(reader.GetOrdinal("data_space_name")),
                    reader.GetString(reader.GetOrdinal("column_name")),
                    reader.GetBoolean(reader.GetOrdinal("is_descending_key"))));
            }
        }

        var map = new Dictionary<int, List<TableScriptRenderer.KeyConstraintGroup>>();
        foreach (var byOid in rows.GroupBy(r => r.Oid))
        {
            var groups = byOid
                .GroupBy(r => r.Name)
                .Select(g => new TableScriptRenderer.KeyConstraintGroup(
                    Name:          g.Key,
                    Type:          g.First().Type,
                    IndexType:     g.First().IndexType,
                    FillFactor:    g.First().FillFactor,
                    IsPadded:      g.First().IsPadded,
                    DataSpaceName: g.First().DataSpace,
                    Columns:       g.Select(r => (r.Column, r.Desc)).ToList()))
                .ToList();
            map[byOid.Key] = groups;
        }
        return map;
    }

    private static async Task<Dictionary<int, List<TableScriptRenderer.CheckConstraintInfo>>> ReadAllCheckConstraintsAsync(
        SqlConnection conn, string objectIdFilter, IReadOnlyList<SqlParameter>? parameters, CancellationToken ct)
    {
        var map = new Dictionary<int, List<TableScriptRenderer.CheckConstraintInfo>>();
        await using var cmd = BuildScopedCommand(conn, AllTableCheckConstraintsQuery, objectIdFilter, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var oid = reader.GetInt32(0);
            if (!map.TryGetValue(oid, out var list))
            {
                list = new List<TableScriptRenderer.CheckConstraintInfo>();
                map[oid] = list;
            }
            list.Add(new TableScriptRenderer.CheckConstraintInfo(
                Name:                reader.GetString(1),
                Definition:          reader.GetString(2),
                IsNotTrusted:        reader.GetBoolean(3),
                IsNotForReplication: reader.GetBoolean(4)));
        }
        return map;
    }

    private static async Task<Dictionary<int, List<TableScriptRenderer.ForeignKeyGroup>>> ReadAllForeignKeysAsync(
        SqlConnection conn, string objectIdFilter, IReadOnlyList<SqlParameter>? parameters, CancellationToken ct)
    {
        var rows = new List<(int Oid, string Name, bool NotTrusted, bool NFR,
                             string RefSchema, string RefTable,
                             string Column, string RefColumn, string OnDelete, string OnUpdate)>();
        await using (var cmd = BuildScopedCommand(conn, AllTableForeignKeysQuery, objectIdFilter, parameters))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add((
                    reader.GetInt32 (0),
                    reader.GetString(1),
                    reader.GetBoolean(2),
                    reader.GetBoolean(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetString(10)));
            }
        }

        var map = new Dictionary<int, List<TableScriptRenderer.ForeignKeyGroup>>();
        foreach (var byOid in rows.GroupBy(r => r.Oid))
        {
            map[byOid.Key] = byOid
                .GroupBy(r => r.Name)
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
        return map;
    }

    private static async Task<Dictionary<int, List<TableScriptRenderer.IndexGroup>>> ReadAllIndexesAsync(
        SqlConnection conn, string objectIdFilter, IReadOnlyList<SqlParameter>? parameters, CancellationToken ct)
    {
        var rows = new List<(int Oid, string Name, string TypeDesc, bool IsUnique, string? Filter,
                             bool IsIncluded, string Column, bool Desc)>();
        await using (var cmd = BuildScopedCommand(conn, AllTableIndexesQuery, objectIdFilter, parameters))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add((
                    reader.GetInt32 (0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetBoolean(3),
                    SafeString(reader, "filter_definition"),
                    reader.GetBoolean(7),
                    reader.GetString(8),
                    reader.GetBoolean(9)));
            }
        }

        var map = new Dictionary<int, List<TableScriptRenderer.IndexGroup>>();
        foreach (var byOid in rows.GroupBy(r => r.Oid))
        {
            map[byOid.Key] = byOid
                .GroupBy(r => r.Name)
                .Select(g => new TableScriptRenderer.IndexGroup(
                    Name:        g.Key,
                    TypeDesc:    g.First().TypeDesc,
                    IsUnique:    g.First().IsUnique,
                    Filter:      g.First().Filter,
                    KeyCols:     g.Where(r => !r.IsIncluded).Select(r => (r.Column, r.Desc)).ToList(),
                    IncludeCols: g.Where(r =>  r.IsIncluded).Select(r =>  r.Column).ToList()))
                .ToList();
        }
        return map;
    }

    // ─── Reader value helpers (mirror SqlObjectScripter's) ─────────────────

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
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    // ─────────────────── Helpers ───────────────────

    /// <summary>
    /// Returns a connection string with <see cref="FetchPacketSize"/> applied.
    /// Connection-pool-friendly: SqlClient pools by exact connection-string
    /// match, so we keep this one variant stable across the whole snapshot
    /// run and every partition reuses pooled connections.
    /// </summary>
    private static string WithFastPacketSize(string original)
    {
        var csb = new SqlConnectionStringBuilder(original)
        {
            PacketSize = FetchPacketSize
        };
        return csb.ConnectionString;
    }

    private static SqlObjectType TypeCodeToKind(string code) => code switch
    {
        "U"                    => SqlObjectType.Table,
        "P"  or "PC" or "RF"   => SqlObjectType.StoredProcedure,
        "FN" or "FS" or "AF"   => SqlObjectType.ScalarFunction,
        "IF"                   => SqlObjectType.InlineTableFunction,
        "TF" or "FT"           => SqlObjectType.TableValuedFunction,
        "V"                    => SqlObjectType.View,
        "TR"                   => SqlObjectType.Trigger,
        _                      => SqlObjectType.Unknown
    };

    private static async Task<bool> ServerSupportsCompressAsync(SqlConnection conn, CancellationToken ct)
    {
        try
        {
            await using var cmd = new SqlCommand("SELECT COMPRESS(CONVERT(VARBINARY(MAX),'probe'))", conn)
            {
                CommandTimeout = 30
            };
            var _ = await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DecompressUtf16Le(byte[] compressed)
    {
        using var input  = new MemoryStream(compressed);
        using var gz     = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(compressed.Length * 4);
        gz.CopyTo(output);
        return Encoding.Unicode.GetString(output.ToArray());
    }

}
