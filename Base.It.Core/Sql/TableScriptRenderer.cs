namespace Base.It.Core.Sql;

/// <summary>
/// Renders a constraint-aware <c>CREATE TABLE</c> script (plus follow-on
/// <c>CREATE INDEX</c>, <c>ALTER TABLE ADD CONSTRAINT</c>, and trigger
/// blocks) from already-loaded catalog rows. Pure formatting — no SQL
/// connection, no I/O. Two callers share this so the on-disk
/// representation is identical no matter how the data was fetched:
/// <list type="bullet">
///   <item><see cref="SqlObjectScripter"/> uses it for single-object
///         fetches (preview / diff / sync source).</item>
///   <item><see cref="Base.It.Core.Schema.BulkSchemaFetcher"/> uses it
///         during snapshot capture, after a single set of bulk catalog
///         queries pulls every user table's metadata in one round-trip
///         set.</item>
/// </list>
/// </summary>
public static class TableScriptRenderer
{
    // ─── Public data shapes (filled by callers from catalog reads) ─────────
    // Promoted to public so the ALTER planner (in Base.It.Core.Sync.TableAlter)
    // can consume the same records the snapshot capture path produces — one
    // vocabulary for "what a table is" across snapshot, render, and diff.

    public sealed record ColumnInfo(
        string  Name,
        string  TypeName,
        int     MaxLength,
        byte    Precision,
        byte    Scale,
        bool    IsNullable,
        bool    IsIdentity,
        long?   IdentitySeed,
        long?   IdentityIncrement,
        bool    IdentityNotForReplication,
        string? ComputedDefinition,
        bool?   ComputedIsPersisted,
        string? DefaultName,
        string? DefaultDefinition,
        string? CollationName,
        bool    IsRowGuidCol);

    public sealed record KeyConstraintGroup(
        string Name,
        string Type,           // "PK" or "UQ"
        string IndexType,      // CLUSTERED / NONCLUSTERED
        byte   FillFactor,
        bool   IsPadded,
        string DataSpaceName,
        List<(string Column, bool Desc)> Columns);

    public sealed record CheckConstraintInfo(
        string Name, string Definition, bool IsNotTrusted, bool IsNotForReplication);

    public sealed record ForeignKeyGroup(
        string Name, bool IsNotTrusted, bool IsNotForReplication,
        string RefSchema, string RefTable,
        string OnDelete, string OnUpdate,
        List<(string Column, string RefColumn)> Columns);

    public sealed record IndexGroup(
        string Name, string TypeDesc, bool IsUnique, string? Filter,
        List<(string Column, bool Desc)> KeyCols,
        List<string> IncludeCols);

    /// <summary>
    /// Renders the full multi-batch script for a single table:
    /// <c>CREATE TABLE</c> (with inline PK / UQ / CHECK), then a <c>GO</c>,
    /// then one <c>CREATE INDEX … GO</c> per non-key index, one
    /// <c>ALTER TABLE … ADD CONSTRAINT</c> per foreign key (also
    /// <c>GO</c>-terminated), and finally each <c>CREATE TRIGGER</c>
    /// definition verbatim. The result executes cleanly end-to-end
    /// through <see cref="SqlScriptRunner"/>.
    ///
    /// Pass <paramref name="triggers"/> empty when triggers are captured
    /// as their own first-class objects (the snapshot model does this).
    /// </summary>
    public static string Render(
        string schema,
        string name,
        string filegroup,
        IReadOnlyList<ColumnInfo>           columns,
        IReadOnlyList<KeyConstraintGroup>   keyConstraints,
        IReadOnlyList<CheckConstraintInfo>  checkConstraints,
        IReadOnlyList<ForeignKeyGroup>      foreignKeys,
        IReadOnlyList<IndexGroup>           indexes,
        IReadOnlyList<(string Schema, string Name, string Definition)> triggers,
        string? dbCollation)
    {
        if (columns.Count == 0) return string.Empty;

        // Column alignment — SSDT-style: pad [Name] and type-spec columns
        // to their max width so everything after lines up cleanly.
        var nameField = columns.Select(c => $"[{c.Name}]").ToList();
        var typeField = columns.Select(RenderTypeSpec).ToList();
        var maxName   = nameField.Max(s => s.Length);
        var maxType   = typeField.Max(s => s.Length);

        var sb = new System.Text.StringBuilder(capacity: 1024);

        // --- CREATE TABLE body: columns + inline PK/UQ/CHECK lines. -----
        sb.Append("CREATE TABLE [").Append(schema).Append("].[").Append(name).Append("] (\n");
        var bodyLines = new List<string>(columns.Count + keyConstraints.Count + checkConstraints.Count);
        for (int i = 0; i < columns.Count; i++)
            bodyLines.Add(RenderColumn(columns[i], nameField[i], typeField[i], maxName, maxType, dbCollation));
        foreach (var k in keyConstraints)   bodyLines.Add(RenderKeyConstraint(k));
        foreach (var c in checkConstraints) bodyLines.Add(RenderCheckConstraint(c));
        sb.Append(string.Join(",\n", bodyLines));
        sb.Append("\n)");
        if (!string.IsNullOrEmpty(filegroup)
            && !string.Equals(filegroup, "PRIMARY", StringComparison.OrdinalIgnoreCase))
            sb.Append(" ON [").Append(filegroup).Append(']');
        sb.Append(";\nGO\n");

        // --- Non-PK/UQ indexes as CREATE INDEX. -------------------------
        foreach (var ix in indexes)
            sb.Append(RenderIndex(ix, schema, name)).Append("GO\n");

        // --- Foreign keys as ALTER TABLE ADD CONSTRAINT. ----------------
        foreach (var fk in foreignKeys)
            sb.Append(RenderForeignKey(fk, schema, name)).Append("GO\n");

        // --- Triggers on this table, verbatim from sys.sql_modules. -----
        // Snapshot model captures triggers as their own objects (type=TR)
        // so the snapshot caller passes an empty list here; the ad-hoc
        // scripter still embeds them for a self-contained file.
        foreach (var (_, _, definition) in triggers)
        {
            sb.Append(definition.TrimEnd());
            sb.Append("\nGO\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders a <c>CREATE TYPE ... AS TABLE (...)</c> definition — same
    /// column / PK / UQ / CHECK body a real table gets, wrapped in the
    /// type-declaration syntax instead of <c>CREATE TABLE</c>. Table types
    /// can't have foreign keys, extra indexes, triggers, or a filegroup,
    /// so those inputs are ignored.
    /// </summary>
    public static string RenderTableType(
        string schema,
        string name,
        IReadOnlyList<ColumnInfo>          columns,
        IReadOnlyList<KeyConstraintGroup>  keyConstraints,
        IReadOnlyList<CheckConstraintInfo> checkConstraints,
        string? dbCollation)
    {
        if (columns.Count == 0) return string.Empty;

        var nameField = columns.Select(c => $"[{c.Name}]").ToList();
        var typeField = columns.Select(RenderTypeSpec).ToList();
        var maxName   = nameField.Max(s => s.Length);
        var maxType   = typeField.Max(s => s.Length);

        var sb = new System.Text.StringBuilder(capacity: 512);
        sb.Append("CREATE TYPE [").Append(schema).Append("].[").Append(name).Append("] AS TABLE (\n");

        var bodyLines = new List<string>(columns.Count + keyConstraints.Count + checkConstraints.Count);
        for (int i = 0; i < columns.Count; i++)
            bodyLines.Add(RenderColumn(columns[i], nameField[i], typeField[i], maxName, maxType, dbCollation, forTableType: true));
        // Table-type constraints are UNNAMED and option-free — use the
        // table-type-specific renderers, NOT the real-table ones (which
        // emit CONSTRAINT [name] ... ON [filegroup], both illegal here).
        foreach (var k in keyConstraints)   bodyLines.Add(RenderTableTypeKeyConstraint(k));
        foreach (var c in checkConstraints) bodyLines.Add(RenderTableTypeCheckConstraint(c));

        sb.Append(string.Join(",\n", bodyLines));
        sb.Append("\n);\nGO\n");
        return sb.ToString();
    }

    /// <summary>
    /// Renders a user-defined data type (alias type):
    /// <c>CREATE TYPE [dbo].[Money2] FROM DECIMAL(19,4) NOT NULL;</c>
    /// Length / precision / scale semantics match <see cref="RenderTypeSpec"/>.
    /// </summary>
    public static string RenderUserDefinedDataType(
        string schema, string name,
        string baseTypeName, int maxLength, byte precision, byte scale, bool isNullable)
    {
        var typeSpec = RenderTypeSpec(new ColumnInfo(
            Name: name,
            TypeName: baseTypeName,
            MaxLength: (short)maxLength,
            Precision: precision,
            Scale: scale,
            IsNullable: isNullable,
            IsIdentity: false, IdentitySeed: null, IdentityIncrement: null, IdentityNotForReplication: false,
            ComputedDefinition: null, ComputedIsPersisted: null,
            DefaultName: null, DefaultDefinition: null,
            CollationName: null, IsRowGuidCol: false));
        var nullability = isNullable ? "NULL" : "NOT NULL";
        return $"CREATE TYPE [{schema}].[{name}] FROM {typeSpec} {nullability};\nGO\n";
    }

    // ─── Per-element rendering helpers ─────────────────────────────────────

    /// <param name="forTableType">
    /// True when rendering a column inside a <c>CREATE TYPE ... AS TABLE</c>.
    /// Table types forbid NAMED constraints, so an inline DEFAULT is
    /// emitted without its <c>CONSTRAINT [name]</c> prefix (the value is
    /// unchanged — only the name is dropped). Default false preserves the
    /// exact real-table output.
    /// </param>
    public static string RenderColumn(
        ColumnInfo c, string nameField, string typeField, int maxName, int maxType, string? dbCollation,
        bool forTableType = false)
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
            // Named DEFAULT constraints are illegal inside a table type —
            // emit the value without the CONSTRAINT [name] prefix there.
            if (c.DefaultName is not null && !forTableType)
                sb.Append("CONSTRAINT [").Append(c.DefaultName).Append("] ");
            sb.Append("DEFAULT ").Append(c.DefaultDefinition);
        }

        sb.Append(c.IsNullable ? " NULL" : " NOT NULL");
        return sb.ToString();
    }

    public static string RenderTypeSpec(ColumnInfo c)
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

    public static bool IsStringLikeType(string t) => t.ToLowerInvariant() is
        "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext";

    public static string RenderKeyConstraint(KeyConstraintGroup k)
    {
        var kind = k.Type.Equals("PK", StringComparison.OrdinalIgnoreCase)
            ? "PRIMARY KEY"
            : "UNIQUE";
        var cols = string.Join(", ", k.Columns.Select(c => $"[{c.Column}] {(c.Desc ? "DESC" : "ASC")}"));
        var with = new List<string>();
        if (k.FillFactor > 0) with.Add($"FILLFACTOR = {k.FillFactor}");
        if (k.IsPadded)       with.Add("PAD_INDEX = ON");
        var withClause = with.Count == 0 ? "" : $" WITH ({string.Join(", ", with)})";
        var onClause   = string.IsNullOrEmpty(k.DataSpaceName)
                         || k.DataSpaceName.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $" ON [{k.DataSpaceName}]";
        return $"    CONSTRAINT [{k.Name}] {kind} {k.IndexType} ({cols}){withClause}{onClause}";
    }

    public static string RenderCheckConstraint(CheckConstraintInfo c)
    {
        var nfr = c.IsNotForReplication ? " NOT FOR REPLICATION" : "";
        return $"    CONSTRAINT [{c.Name}] CHECK{nfr} {c.Definition}";
    }

    /// <summary>
    /// PK / UNIQUE as it must appear INSIDE a <c>CREATE TYPE ... AS TABLE</c>:
    /// UNNAMED, and without the <c>ON [filegroup]</c> / FILLFACTOR / PAD_INDEX
    /// clauses. SQL Server rejects all of those in a table type — which is
    /// exactly why <see cref="RenderKeyConstraint"/> (correct for a real
    /// table) can't be reused here. CLUSTERED / NONCLUSTERED is kept because
    /// it IS allowed. The auto-generated catalog constraint name is dropped
    /// intentionally: a table type's PK is anonymous, so echoing the
    /// server's internal name back in the CREATE would fail.
    /// </summary>
    public static string RenderTableTypeKeyConstraint(KeyConstraintGroup k)
    {
        var kind = k.Type.Equals("PK", StringComparison.OrdinalIgnoreCase)
            ? "PRIMARY KEY"
            : "UNIQUE";
        var cols = string.Join(", ", k.Columns.Select(c => $"[{c.Column}] {(c.Desc ? "DESC" : "ASC")}"));
        return $"    {kind} {k.IndexType} ({cols})";
    }

    /// <summary>
    /// CHECK as it must appear inside a table type: UNNAMED (and NOT FOR
    /// REPLICATION doesn't apply to table types). The definition text from
    /// the catalog already carries its own parentheses.
    /// </summary>
    public static string RenderTableTypeCheckConstraint(CheckConstraintInfo c)
        => $"    CHECK {c.Definition}";

    public static string RenderForeignKey(ForeignKeyGroup fk, string parentSchema, string parentTable)
    {
        var cols    = string.Join(", ", fk.Columns.Select(c => $"[{c.Column}]"));
        var refCols = string.Join(", ", fk.Columns.Select(c => $"[{c.RefColumn}]"));
        var check   = fk.IsNotTrusted ? "WITH NOCHECK" : "WITH CHECK";
        var nfr     = fk.IsNotForReplication ? " NOT FOR REPLICATION" : "";
        var onDel   = fk.OnDelete.Equals("NO_ACTION", StringComparison.OrdinalIgnoreCase)
            ? "" : $" ON DELETE {fk.OnDelete.Replace('_', ' ')}";
        var onUpd   = fk.OnUpdate.Equals("NO_ACTION", StringComparison.OrdinalIgnoreCase)
            ? "" : $" ON UPDATE {fk.OnUpdate.Replace('_', ' ')}";
        return $"ALTER TABLE [{parentSchema}].[{parentTable}] {check} ADD CONSTRAINT [{fk.Name}] " +
               $"FOREIGN KEY ({cols}) REFERENCES [{fk.RefSchema}].[{fk.RefTable}] ({refCols})" +
               $"{nfr}{onDel}{onUpd};\n";
    }

    public static string RenderIndex(IndexGroup ix, string parentSchema, string parentTable)
    {
        var unique  = ix.IsUnique ? "UNIQUE " : "";
        var keyCols = string.Join(", ", ix.KeyCols.Select(c => $"[{c.Column}] {(c.Desc ? "DESC" : "ASC")}"));
        var include = ix.IncludeCols.Count == 0
            ? ""
            : $" INCLUDE ({string.Join(", ", ix.IncludeCols.Select(c => $"[{c}]"))})";
        var filter  = string.IsNullOrWhiteSpace(ix.Filter) ? "" : $" WHERE {ix.Filter}";
        return $"CREATE {unique}{ix.TypeDesc} INDEX [{ix.Name}] " +
               $"ON [{parentSchema}].[{parentTable}] ({keyCols}){include}{filter};\n";
    }
}
