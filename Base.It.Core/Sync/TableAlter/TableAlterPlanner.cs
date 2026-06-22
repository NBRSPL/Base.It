using Base.It.Core.Models;
using Base.It.Core.Sql;

namespace Base.It.Core.Sync.TableAlter;

/// <summary>
/// Pure-function differ that compares a source table's metadata against
/// a target table's current metadata and produces an
/// <see cref="AlterPlan"/> — the partitioned list of safe / destructive
/// <see cref="AlterStep"/>s required to make target match source.
///
/// Safety is the load-bearing property: every classification decision
/// has a comment explaining why it's safe (or unsafe). When in doubt,
/// the planner errs toward "destructive" — better to surface a step
/// for confirmation than to silently apply something that loses data.
///
/// The planner is intentionally conservative for v1:
/// <list type="bullet">
///   <item><b>Safe</b>: add nullable column · add column with DEFAULT ·
///         widen string/numeric type · NOT NULL → NULL · add CHECK / FK
///         WITH NOCHECK · add / drop non-key index · drop CHECK / FK / UQ ·
///         change default constraint.</item>
///   <item><b>Destructive</b>: drop column · narrow / incompatible type
///         change · NULL → NOT NULL without default · identity change ·
///         computed column change · PK changes · ADD UQ / PK (could
///         fail on existing duplicate data).</item>
/// </list>
/// </summary>
public static class TableAlterPlanner
{
    /// <summary>
    /// Compare <paramref name="source"/> to <paramref name="target"/> and
    /// produce the plan that would bring target into structural parity
    /// with source. Both inputs must describe the SAME logical table
    /// (caller-enforced — the table name is taken from
    /// <paramref name="source"/>); a name mismatch isn't validated here
    /// because the caller has already resolved the
    /// <see cref="ObjectIdentifier"/> on both ends.
    /// </summary>
    public static AlterPlan Plan(TableMetadata source, TableMetadata target)
    {
        var schema = source.Schema;
        var name   = source.Name;
        var id     = new ObjectIdentifier(schema, name);
        var safe   = new List<AlterStep>();
        var dest   = new List<AlterStep>();

        DiffColumns          (schema, name, source, target, safe, dest);
        DiffKeyConstraints   (schema, name, source, target, safe, dest);
        DiffCheckConstraints (schema, name, source, target, safe, dest);
        DiffForeignKeys      (schema, name, source, target, safe, dest);
        DiffIndexes          (schema, name, source, target, safe, dest);

        return new AlterPlan(id, safe, dest);
    }

    // ─── Columns ──────────────────────────────────────────────────────────

    private static void DiffColumns(
        string schema, string name,
        TableMetadata source, TableMetadata target,
        List<AlterStep> safe, List<AlterStep> dest)
    {
        var srcByName = source.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var tgtByName = target.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        // Columns to ADD (in source, not in target). Identity-bearing
        // adds are destructive — you can't backfill an IDENTITY column on
        // an existing table without rewriting data.
        foreach (var sc in source.Columns)
        {
            if (tgtByName.ContainsKey(sc.Name)) continue;

            if (sc.IsIdentity)
            {
                dest.Add(new AlterStep(
                    AlterStepKind.AddColumn,
                    AddColumnSql(schema, name, sc, source.DatabaseCollation),
                    $"ADD COLUMN [{sc.Name}] (identity)",
                    IsDestructive: true,
                    DestructiveReason: "Cannot add an IDENTITY column to a non-empty table without recreating it. Apply only if the target table is empty."));
                continue;
            }

            if (!sc.IsNullable && sc.DefaultDefinition is null && sc.ComputedDefinition is null)
            {
                dest.Add(new AlterStep(
                    AlterStepKind.AddColumn,
                    AddColumnSql(schema, name, sc, source.DatabaseCollation),
                    $"ADD COLUMN [{sc.Name}] NOT NULL (no default)",
                    IsDestructive: true,
                    DestructiveReason: "Adding a NOT NULL column without a DEFAULT will fail on a non-empty table. Add a DEFAULT to the source, or apply only on an empty target."));
                continue;
            }

            safe.Add(new AlterStep(
                AlterStepKind.AddColumn,
                AddColumnSql(schema, name, sc, source.DatabaseCollation),
                $"ADD COLUMN [{sc.Name}] {TableScriptRenderer.RenderTypeSpec(sc)}",
                IsDestructive: false,
                DestructiveReason: null));
        }

        // Columns to DROP (in target, not in source). Always destructive —
        // dropping a column removes the data it held. We emit the step so
        // the user can opt in via the per-destructive preview, but batch
        // never applies it automatically.
        foreach (var tc in target.Columns)
        {
            if (srcByName.ContainsKey(tc.Name)) continue;

            // If the column has a default constraint, drop that FIRST so
            // the DROP COLUMN doesn't fail with "object depends on default."
            if (tc.DefaultName is not null)
            {
                dest.Add(new AlterStep(
                    AlterStepKind.DropDefault,
                    $"ALTER TABLE [{schema}].[{name}] DROP CONSTRAINT [{tc.DefaultName}];",
                    $"DROP DEFAULT [{tc.DefaultName}] (precursor to dropping [{tc.Name}])",
                    IsDestructive: true,
                    DestructiveReason: "Precursor to a column drop — only runs if the column drop is also approved."));
            }
            dest.Add(new AlterStep(
                AlterStepKind.DropColumn,
                $"ALTER TABLE [{schema}].[{name}] DROP COLUMN [{tc.Name}];",
                $"DROP COLUMN [{tc.Name}]",
                IsDestructive: true,
                DestructiveReason: $"Dropping a column destroys all data stored in [{tc.Name}]. Cannot be undone except by restoring the backup."));
        }

        // Columns in BOTH — check for shape diffs.
        foreach (var sc in source.Columns)
        {
            if (!tgtByName.TryGetValue(sc.Name, out var tc)) continue;
            if (ColumnsStructurallyEqual(sc, tc, source.DatabaseCollation, target.DatabaseCollation)) continue;

            ClassifyColumnChange(schema, name, sc, tc, source.DatabaseCollation, safe, dest);
        }
    }

    private static bool ColumnsStructurallyEqual(
        TableScriptRenderer.ColumnInfo a, TableScriptRenderer.ColumnInfo b,
        string? aCollation, string? bCollation)
    {
        // Compare every meaningful field. Use OrdinalIgnoreCase for SQL
        // identifiers and the empty-string-equivalence for nullable types.
        if (!string.Equals(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase)) return false;
        if (a.MaxLength != b.MaxLength) return false;
        if (a.Precision != b.Precision) return false;
        if (a.Scale     != b.Scale)     return false;
        if (a.IsNullable != b.IsNullable) return false;
        if (a.IsIdentity != b.IsIdentity) return false;
        if (a.IdentitySeed != b.IdentitySeed) return false;
        if (a.IdentityIncrement != b.IdentityIncrement) return false;
        if (a.IdentityNotForReplication != b.IdentityNotForReplication) return false;
        if (!string.Equals(a.ComputedDefinition ?? "", b.ComputedDefinition ?? "", StringComparison.Ordinal)) return false;
        if (a.ComputedIsPersisted != b.ComputedIsPersisted) return false;
        if (!string.Equals(a.DefaultDefinition ?? "", b.DefaultDefinition ?? "", StringComparison.Ordinal)) return false;
        if (a.IsRowGuidCol != b.IsRowGuidCol) return false;

        // Collation: a null on either side means "matches db default."
        // Normalise to the db default before comparing so we don't flag a
        // diff when neither side overrides it explicitly.
        var aColl = a.CollationName ?? aCollation;
        var bColl = b.CollationName ?? bCollation;
        if (!string.Equals(aColl ?? "", bColl ?? "", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    private static void ClassifyColumnChange(
        string schema, string name,
        TableScriptRenderer.ColumnInfo sc, TableScriptRenderer.ColumnInfo tc,
        string? sourceDbCollation,
        List<AlterStep> safe, List<AlterStep> dest)
    {
        // ─── Hard "destructive" cases (no clean safe rewrite) ──────────
        if (sc.IsIdentity != tc.IsIdentity || sc.IdentitySeed != tc.IdentitySeed || sc.IdentityIncrement != tc.IdentityIncrement)
        {
            dest.Add(new AlterStep(
                AlterStepKind.AlterColumn,
                $"-- Not auto-generated. Identity changes require recreating the table.\n-- Source:  IDENTITY({sc.IdentitySeed ?? 1},{sc.IdentityIncrement ?? 1}); Target: IDENTITY({tc.IdentitySeed ?? 1},{tc.IdentityIncrement ?? 1})",
                $"Column [{sc.Name}] identity changed",
                IsDestructive: true,
                DestructiveReason: "IDENTITY can't be altered in-place — would require dropping and recreating the table (data loss)."));
            return;
        }

        if (!string.Equals(sc.ComputedDefinition ?? "", tc.ComputedDefinition ?? "", StringComparison.Ordinal)
            || sc.ComputedIsPersisted != tc.ComputedIsPersisted)
        {
            dest.Add(new AlterStep(
                AlterStepKind.AlterColumn,
                $"-- Computed column change for [{sc.Name}] needs a drop + recreate.",
                $"Column [{sc.Name}] computed expression changed",
                IsDestructive: true,
                DestructiveReason: "Changing a computed-column expression requires dropping and re-adding the column."));
            return;
        }

        // Nullability — NULL → NOT NULL is destructive unless source has
        // a default. NOT NULL → NULL is always safe.
        if (sc.IsNullable && !tc.IsNullable)
        {
            // Source nullable, target NOT NULL → relax target to NULL. Safe.
        }
        else if (!sc.IsNullable && tc.IsNullable)
        {
            // Source NOT NULL, target NULL → tighten. Will fail if target has nulls.
            if (sc.DefaultDefinition is null)
            {
                dest.Add(new AlterStep(
                    AlterStepKind.AlterColumn,
                    AlterColumnSql(schema, name, sc, sourceDbCollation),
                    $"Tighten [{sc.Name}] to NOT NULL (no default)",
                    IsDestructive: true,
                    DestructiveReason: "Tightening nullability fails if the target column contains any NULL rows. Add a DEFAULT in source or backfill the column first."));
                return;
            }
        }

        // Type change classification.
        var (typeChanged, typeIsSafe, reason) = ClassifyTypeChange(sc, tc);
        if (typeChanged && !typeIsSafe)
        {
            dest.Add(new AlterStep(
                AlterStepKind.AlterColumn,
                AlterColumnSql(schema, name, sc, sourceDbCollation),
                $"Change [{sc.Name}] type to {TableScriptRenderer.RenderTypeSpec(sc)}",
                IsDestructive: true,
                DestructiveReason: reason ?? "Type change is not provably safe."));
            return;
        }

        // ─── Safe ALTER path ───────────────────────────────────────────
        // Default constraints are handled separately (drop old + add new)
        // so the ALTER COLUMN itself doesn't have to deal with them.
        if (!string.Equals(sc.DefaultDefinition ?? "", tc.DefaultDefinition ?? "", StringComparison.Ordinal))
        {
            if (tc.DefaultName is not null)
            {
                safe.Add(new AlterStep(
                    AlterStepKind.DropDefault,
                    $"ALTER TABLE [{schema}].[{name}] DROP CONSTRAINT [{tc.DefaultName}];",
                    $"DROP DEFAULT on [{sc.Name}]",
                    IsDestructive: false, DestructiveReason: null));
            }
            if (sc.DefaultDefinition is not null)
            {
                var defName = sc.DefaultName ?? $"DF_{name}_{sc.Name}";
                safe.Add(new AlterStep(
                    AlterStepKind.AddDefault,
                    $"ALTER TABLE [{schema}].[{name}] ADD CONSTRAINT [{defName}] DEFAULT {sc.DefaultDefinition} FOR [{sc.Name}];",
                    $"ADD DEFAULT on [{sc.Name}]: {sc.DefaultDefinition}",
                    IsDestructive: false, DestructiveReason: null));
            }
        }

        // Did anything besides the default actually change? If only the
        // default differed, the two ADD/DROP DEFAULT steps above are enough.
        if (typeChanged || sc.IsNullable != tc.IsNullable
            || !string.Equals(sc.CollationName ?? sourceDbCollation ?? "",
                              tc.CollationName ?? sourceDbCollation ?? "",
                              StringComparison.OrdinalIgnoreCase))
        {
            safe.Add(new AlterStep(
                AlterStepKind.AlterColumn,
                AlterColumnSql(schema, name, sc, sourceDbCollation),
                $"ALTER [{sc.Name}] → {TableScriptRenderer.RenderTypeSpec(sc)} {(sc.IsNullable ? "NULL" : "NOT NULL")}",
                IsDestructive: false, DestructiveReason: null));
        }
    }

    /// <summary>
    /// Decide whether a type / size change is safe to apply via
    /// <c>ALTER COLUMN</c> without risking data loss.
    /// Returns <c>(typeChanged, isSafe, destructiveReasonIfUnsafe)</c>.
    /// </summary>
    private static (bool typeChanged, bool isSafe, string? destructiveReason) ClassifyTypeChange(
        TableScriptRenderer.ColumnInfo sc, TableScriptRenderer.ColumnInfo tc)
    {
        var sameType = string.Equals(sc.TypeName, tc.TypeName, StringComparison.OrdinalIgnoreCase);
        var sameSize = sc.MaxLength == tc.MaxLength && sc.Precision == tc.Precision && sc.Scale == tc.Scale;
        if (sameType && sameSize) return (false, true, null);

        var srcType = sc.TypeName.ToLowerInvariant();
        var tgtType = tc.TypeName.ToLowerInvariant();

        // Same-type widening. MaxLength = -1 means MAX → unbounded.
        if (sameType)
        {
            if (srcType is "varchar" or "char" or "nvarchar" or "nchar" or "binary" or "varbinary")
            {
                // MAX always wins over any bounded size; otherwise needs >= existing.
                if (sc.MaxLength == -1 && tc.MaxLength != -1) return (true, true, null);
                if (tc.MaxLength == -1 && sc.MaxLength != -1)
                    return (true, false, $"Narrowing [{sc.Name}] from MAX to a bounded size could truncate data.");
                if (sc.MaxLength >= tc.MaxLength) return (true, true, null);
                return (true, false, $"Narrowing [{sc.Name}] from size {tc.MaxLength} to {sc.MaxLength} could truncate data.");
            }
            if (srcType is "decimal" or "numeric")
            {
                if (sc.Precision >= tc.Precision && sc.Scale >= tc.Scale) return (true, true, null);
                return (true, false, $"Narrowing decimal precision/scale for [{sc.Name}] could lose data.");
            }
            if (srcType is "datetime2" or "datetimeoffset" or "time")
            {
                if (sc.Scale >= tc.Scale) return (true, true, null);
                return (true, false, $"Reducing fractional-second precision for [{sc.Name}] could truncate.");
            }
            // Other same-type same-name with different size we don't recognise — be safe.
            return (true, false, $"Unrecognised size change for [{sc.Name}] of type {srcType}.");
        }

        // Cross-type widening within the integer family.
        if (IsIntegerWidening(tgtType, srcType)) return (true, true, null);

        // varchar → nvarchar (same length or larger) — safe; nvarchar can hold every varchar value.
        if (srcType is "nvarchar" or "nchar" && tgtType is "varchar" or "char")
        {
            var sLen = sc.MaxLength == -1 ? int.MaxValue : (srcType == "nvarchar" || srcType == "nchar" ? sc.MaxLength / 2 : sc.MaxLength);
            var tLen = tc.MaxLength == -1 ? int.MaxValue : tc.MaxLength;
            if (sLen >= tLen) return (true, true, null);
            return (true, false, $"Widening [{sc.Name}] from {tgtType} to {srcType} would shrink storage size.");
        }

        // Everything else: unknown territory, be conservative.
        return (true, false, $"Type change [{sc.Name}] {tgtType} → {srcType} is not provably safe.");
    }

    private static bool IsIntegerWidening(string from, string to)
    {
        int Rank(string t) => t switch
        {
            "tinyint"  => 1,
            "smallint" => 2,
            "int"      => 3,
            "bigint"   => 4,
            _          => 0
        };
        var a = Rank(from);
        var b = Rank(to);
        return a > 0 && b > 0 && b >= a;
    }

    // ─── Key constraints (PK + UQ) ────────────────────────────────────────

    private static void DiffKeyConstraints(
        string schema, string name,
        TableMetadata source, TableMetadata target,
        List<AlterStep> safe, List<AlterStep> dest)
    {
        var srcByName = source.KeyConstraints.ToDictionary(k => k.Name, StringComparer.OrdinalIgnoreCase);
        var tgtByName = target.KeyConstraints.ToDictionary(k => k.Name, StringComparer.OrdinalIgnoreCase);

        // Drop UQ/PK constraints not in source. Dropping is data-preserving
        // (no rows removed) so it's classified safe — BUT a PK drop can
        // break FKs from other tables. We still classify "safe" for the
        // table itself and let SQL Server reject the transaction if a
        // cross-table FK depends on this PK; the rollback then surfaces
        // exactly which dependency stopped us. That's safer than refusing
        // pre-emptively and missing the legitimate case.
        foreach (var tk in target.KeyConstraints)
        {
            if (srcByName.ContainsKey(tk.Name)) continue;
            safe.Add(new AlterStep(
                AlterStepKind.DropUniqueOrPk,
                $"ALTER TABLE [{schema}].[{name}] DROP CONSTRAINT [{tk.Name}];",
                $"DROP {tk.Type} [{tk.Name}]",
                IsDestructive: false, DestructiveReason: null));
        }

        // Add UQ/PK constraints not in target. Always destructive: ADD
        // PRIMARY KEY / UNIQUE will FAIL if existing rows violate the
        // constraint, and unlike CHECK/FK, SQL Server doesn't support
        // WITH NOCHECK on these. The user has to confirm.
        foreach (var sk in source.KeyConstraints)
        {
            if (tgtByName.ContainsKey(sk.Name)) continue;
            dest.Add(new AlterStep(
                AlterStepKind.AddUniqueOrPk,
                $"ALTER TABLE [{schema}].[{name}] ADD {RenderKeyAsAlter(sk)};",
                $"ADD {sk.Type} [{sk.Name}]",
                IsDestructive: true,
                DestructiveReason: $"Adding a {(sk.Type.Equals("PK", StringComparison.OrdinalIgnoreCase) ? "PRIMARY KEY" : "UNIQUE constraint")} fails if existing target rows violate uniqueness. SQL Server does not support WITH NOCHECK on PK/UQ — apply only after confirming no duplicates."));
        }

        // Changed PK/UQ — drop + re-add. Destructive because the ADD half
        // can fail (see above).
        foreach (var sk in source.KeyConstraints)
        {
            if (!tgtByName.TryGetValue(sk.Name, out var tk)) continue;
            if (KeyConstraintsEqual(sk, tk)) continue;

            safe.Add(new AlterStep(
                AlterStepKind.DropUniqueOrPk,
                $"ALTER TABLE [{schema}].[{name}] DROP CONSTRAINT [{sk.Name}];",
                $"DROP {sk.Type} [{sk.Name}] (precursor to re-add)",
                IsDestructive: false, DestructiveReason: null));
            dest.Add(new AlterStep(
                AlterStepKind.AddUniqueOrPk,
                $"ALTER TABLE [{schema}].[{name}] ADD {RenderKeyAsAlter(sk)};",
                $"RE-ADD {sk.Type} [{sk.Name}] (definition changed)",
                IsDestructive: true,
                DestructiveReason: "Re-creating a PK/UQ with new columns can fail if existing rows violate the new uniqueness."));
        }
    }

    private static bool KeyConstraintsEqual(
        TableScriptRenderer.KeyConstraintGroup a,
        TableScriptRenderer.KeyConstraintGroup b)
    {
        if (!string.Equals(a.Type, b.Type, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(a.IndexType, b.IndexType, StringComparison.OrdinalIgnoreCase)) return false;
        if (a.FillFactor != b.FillFactor) return false;
        if (a.IsPadded   != b.IsPadded)   return false;
        if (a.Columns.Count != b.Columns.Count) return false;
        for (int i = 0; i < a.Columns.Count; i++)
        {
            if (!string.Equals(a.Columns[i].Column, b.Columns[i].Column, StringComparison.OrdinalIgnoreCase)) return false;
            if (a.Columns[i].Desc != b.Columns[i].Desc) return false;
        }
        return true;
    }

    // ─── Check constraints ────────────────────────────────────────────────

    private static void DiffCheckConstraints(
        string schema, string name,
        TableMetadata source, TableMetadata target,
        List<AlterStep> safe, List<AlterStep> dest)
    {
        var srcByName = source.CheckConstraints.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var tgtByName = target.CheckConstraints.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var tc in target.CheckConstraints)
        {
            if (srcByName.ContainsKey(tc.Name)) continue;
            safe.Add(new AlterStep(
                AlterStepKind.DropCheckConstraint,
                $"ALTER TABLE [{schema}].[{name}] DROP CONSTRAINT [{tc.Name}];",
                $"DROP CHECK [{tc.Name}]",
                IsDestructive: false, DestructiveReason: null));
        }
        foreach (var sc in source.CheckConstraints)
        {
            if (tgtByName.ContainsKey(sc.Name)) continue;
            // WITH NOCHECK so the constraint is added but existing rows
            // aren't re-validated. Future writes WILL be validated. This
            // is the safe way to surface a new CHECK on an existing table.
            safe.Add(new AlterStep(
                AlterStepKind.AddCheckConstraint,
                $"ALTER TABLE [{schema}].[{name}] WITH NOCHECK ADD CONSTRAINT [{sc.Name}] CHECK{(sc.IsNotForReplication ? " NOT FOR REPLICATION" : "")} {sc.Definition};",
                $"ADD CHECK [{sc.Name}] WITH NOCHECK",
                IsDestructive: false, DestructiveReason: null));
        }
        foreach (var sc in source.CheckConstraints)
        {
            if (!tgtByName.TryGetValue(sc.Name, out var tc)) continue;
            if (string.Equals(sc.Definition, tc.Definition, StringComparison.Ordinal)
                && sc.IsNotForReplication == tc.IsNotForReplication) continue;

            safe.Add(new AlterStep(
                AlterStepKind.DropCheckConstraint,
                $"ALTER TABLE [{schema}].[{name}] DROP CONSTRAINT [{sc.Name}];",
                $"DROP CHECK [{sc.Name}] (precursor to re-add)",
                IsDestructive: false, DestructiveReason: null));
            safe.Add(new AlterStep(
                AlterStepKind.AddCheckConstraint,
                $"ALTER TABLE [{schema}].[{name}] WITH NOCHECK ADD CONSTRAINT [{sc.Name}] CHECK{(sc.IsNotForReplication ? " NOT FOR REPLICATION" : "")} {sc.Definition};",
                $"RE-ADD CHECK [{sc.Name}] (definition changed) WITH NOCHECK",
                IsDestructive: false, DestructiveReason: null));
        }
    }

    // ─── Foreign keys ─────────────────────────────────────────────────────

    private static void DiffForeignKeys(
        string schema, string name,
        TableMetadata source, TableMetadata target,
        List<AlterStep> safe, List<AlterStep> dest)
    {
        var srcByName = source.ForeignKeys.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var tgtByName = target.ForeignKeys.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var tf in target.ForeignKeys)
        {
            if (srcByName.ContainsKey(tf.Name)) continue;
            safe.Add(new AlterStep(
                AlterStepKind.DropForeignKey,
                $"ALTER TABLE [{schema}].[{name}] DROP CONSTRAINT [{tf.Name}];",
                $"DROP FK [{tf.Name}]",
                IsDestructive: false, DestructiveReason: null));
        }
        foreach (var sf in source.ForeignKeys)
        {
            if (tgtByName.ContainsKey(sf.Name)) continue;
            safe.Add(new AlterStep(
                AlterStepKind.AddForeignKey,
                RenderFkAsAlter(schema, name, sf, withNoCheck: true),
                $"ADD FK [{sf.Name}] WITH NOCHECK",
                IsDestructive: false, DestructiveReason: null));
        }
        foreach (var sf in source.ForeignKeys)
        {
            if (!tgtByName.TryGetValue(sf.Name, out var tf)) continue;
            if (ForeignKeysEqual(sf, tf)) continue;

            safe.Add(new AlterStep(
                AlterStepKind.DropForeignKey,
                $"ALTER TABLE [{schema}].[{name}] DROP CONSTRAINT [{sf.Name}];",
                $"DROP FK [{sf.Name}] (precursor to re-add)",
                IsDestructive: false, DestructiveReason: null));
            safe.Add(new AlterStep(
                AlterStepKind.AddForeignKey,
                RenderFkAsAlter(schema, name, sf, withNoCheck: true),
                $"RE-ADD FK [{sf.Name}] (definition changed) WITH NOCHECK",
                IsDestructive: false, DestructiveReason: null));
        }
    }

    private static bool ForeignKeysEqual(
        TableScriptRenderer.ForeignKeyGroup a,
        TableScriptRenderer.ForeignKeyGroup b)
    {
        if (!string.Equals(a.RefSchema, b.RefSchema, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(a.RefTable,  b.RefTable,  StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(a.OnDelete,  b.OnDelete,  StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(a.OnUpdate,  b.OnUpdate,  StringComparison.OrdinalIgnoreCase)) return false;
        if (a.IsNotForReplication != b.IsNotForReplication) return false;
        if (a.Columns.Count != b.Columns.Count) return false;
        for (int i = 0; i < a.Columns.Count; i++)
        {
            if (!string.Equals(a.Columns[i].Column,   b.Columns[i].Column,   StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(a.Columns[i].RefColumn,b.Columns[i].RefColumn,StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    // ─── Indexes (non-key) ────────────────────────────────────────────────

    private static void DiffIndexes(
        string schema, string name,
        TableMetadata source, TableMetadata target,
        List<AlterStep> safe, List<AlterStep> dest)
    {
        var srcByName = source.Indexes.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var tgtByName = target.Indexes.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var ti in target.Indexes)
        {
            if (srcByName.ContainsKey(ti.Name)) continue;
            safe.Add(new AlterStep(
                AlterStepKind.DropIndex,
                $"DROP INDEX [{ti.Name}] ON [{schema}].[{name}];",
                $"DROP INDEX [{ti.Name}]",
                IsDestructive: false, DestructiveReason: null));
        }
        foreach (var si in source.Indexes)
        {
            if (tgtByName.ContainsKey(si.Name)) continue;
            safe.Add(new AlterStep(
                AlterStepKind.AddIndex,
                TableScriptRenderer.RenderIndex(si, schema, name).TrimEnd(),
                $"CREATE INDEX [{si.Name}]",
                IsDestructive: false, DestructiveReason: null));
        }
        foreach (var si in source.Indexes)
        {
            if (!tgtByName.TryGetValue(si.Name, out var ti)) continue;
            if (IndexesEqual(si, ti)) continue;

            safe.Add(new AlterStep(
                AlterStepKind.DropIndex,
                $"DROP INDEX [{si.Name}] ON [{schema}].[{name}];",
                $"DROP INDEX [{si.Name}] (precursor to re-create)",
                IsDestructive: false, DestructiveReason: null));
            safe.Add(new AlterStep(
                AlterStepKind.AddIndex,
                TableScriptRenderer.RenderIndex(si, schema, name).TrimEnd(),
                $"RE-CREATE INDEX [{si.Name}] (definition changed)",
                IsDestructive: false, DestructiveReason: null));
        }
    }

    private static bool IndexesEqual(
        TableScriptRenderer.IndexGroup a,
        TableScriptRenderer.IndexGroup b)
    {
        if (a.IsUnique != b.IsUnique) return false;
        if (!string.Equals(a.TypeDesc, b.TypeDesc, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(a.Filter ?? "", b.Filter ?? "", StringComparison.Ordinal)) return false;
        if (a.KeyCols.Count != b.KeyCols.Count) return false;
        for (int i = 0; i < a.KeyCols.Count; i++)
        {
            if (!string.Equals(a.KeyCols[i].Column, b.KeyCols[i].Column, StringComparison.OrdinalIgnoreCase)) return false;
            if (a.KeyCols[i].Desc != b.KeyCols[i].Desc) return false;
        }
        if (a.IncludeCols.Count != b.IncludeCols.Count) return false;
        for (int i = 0; i < a.IncludeCols.Count; i++)
            if (!string.Equals(a.IncludeCols[i], b.IncludeCols[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // ─── SQL fragment helpers ─────────────────────────────────────────────

    private static string AddColumnSql(
        string schema, string name,
        TableScriptRenderer.ColumnInfo c, string? dbCollation)
    {
        // RenderColumn produces "    [name]    TYPE NOT NULL CONSTRAINT [df] DEFAULT (..)".
        // For ALTER ADD we strip the leading indent.
        var nameField = $"[{c.Name}]";
        var typeField = TableScriptRenderer.RenderTypeSpec(c);
        var line = TableScriptRenderer.RenderColumn(c, nameField, typeField, nameField.Length, typeField.Length, dbCollation);
        return $"ALTER TABLE [{schema}].[{name}] ADD {line.TrimStart()};";
    }

    private static string AlterColumnSql(
        string schema, string name,
        TableScriptRenderer.ColumnInfo c, string? dbCollation)
    {
        // ALTER COLUMN doesn't take DEFAULT — defaults are handled as
        // separate constraint operations. The renderer's column line
        // includes default + identity bits which aren't valid here, so
        // we hand-build the spec from just type + collation + nullability.
        var typeSpec = TableScriptRenderer.RenderTypeSpec(c);
        var sb = new System.Text.StringBuilder();
        sb.Append("ALTER TABLE [").Append(schema).Append("].[").Append(name).Append("] ");
        sb.Append("ALTER COLUMN [").Append(c.Name).Append("] ").Append(typeSpec);
        if (TableScriptRenderer.IsStringLikeType(c.TypeName)
            && !string.IsNullOrEmpty(c.CollationName)
            && !string.Equals(c.CollationName, dbCollation, StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" COLLATE ").Append(c.CollationName);
        }
        sb.Append(c.IsNullable ? " NULL;" : " NOT NULL;");
        return sb.ToString();
    }

    private static string RenderKeyAsAlter(TableScriptRenderer.KeyConstraintGroup k)
    {
        // Reuse the renderer's inline form, strip the leading 4-space
        // indent (it's meant for inside a CREATE TABLE body).
        var inlineForm = TableScriptRenderer.RenderKeyConstraint(k).TrimStart();
        return inlineForm;
    }

    private static string RenderFkAsAlter(
        string parentSchema, string parentTable,
        TableScriptRenderer.ForeignKeyGroup fk,
        bool withNoCheck)
    {
        // The renderer's FK fragment already emits ALTER TABLE … WITH
        // CHECK ADD CONSTRAINT — we just swap WITH CHECK → WITH NOCHECK
        // when we want to skip validating existing rows.
        var rendered = TableScriptRenderer.RenderForeignKey(fk, parentSchema, parentTable).TrimEnd();
        // RenderForeignKey emits "WITH CHECK" or "WITH NOCHECK" based on
        // fk.IsNotTrusted. Override to NOCHECK when caller asks.
        return withNoCheck
            ? rendered.Replace("WITH CHECK ADD CONSTRAINT", "WITH NOCHECK ADD CONSTRAINT", StringComparison.Ordinal) + ";"
            : rendered + ";";
    }
}
