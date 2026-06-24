using System.Text;
using Base.It.Core.Models;

namespace Base.It.Core.Sync.TableAlter;

/// <summary>
/// Turns a chosen subset of <see cref="AlterStep"/>s into one
/// transaction-wrapped T-SQL batch. The wrapper is
/// <c>SET XACT_ABORT ON; BEGIN TRY; BEGIN TRAN; ... ; COMMIT; END TRY;
/// BEGIN CATCH; ROLLBACK; THROW; END CATCH</c> — so any single failure
/// rolls every previous step back. No partial state survives.
///
/// Why a single batch (no <c>GO</c> in the middle)? Two reasons:
/// <list type="bullet">
///   <item>The whole point is atomicity. A <c>GO</c> ends a batch, and
///         a transaction can't span batches in the SQL Server client
///         protocol. Single batch = single transaction.</item>
///   <item>Every step we emit (<c>ALTER TABLE … ADD/DROP/ALTER COLUMN</c>,
///         <c>ALTER TABLE … ADD/DROP CONSTRAINT</c>, <c>CREATE/DROP
///         INDEX</c>) is a DDL statement that's fine in the middle of
///         a multi-statement batch. No <c>CREATE</c> of an object whose
///         name we'd also reference in the same batch (the bare
///         <c>CREATE TABLE</c> case is not on this path).</item>
/// </list>
/// </summary>
public static class AlterScriptBuilder
{
    /// <summary>
    /// Build the SQL for a chosen subset of steps from a plan. Steps are
    /// stable-sorted by <see cref="AlterStep.Kind"/> so the
    /// drop-then-change-then-add ordering rules implicit in the
    /// <see cref="AlterStepKind"/> enum are enforced regardless of the
    /// caller's input order.
    ///
    /// Returns empty string when <paramref name="stepsToApply"/> has no
    /// elements — callers can use this to short-circuit "nothing to do."
    /// </summary>
    public static string Build(
        ObjectIdentifier table,
        IEnumerable<AlterStep> stepsToApply)
    {
        var ordered = stepsToApply.OrderBy(s => (int)s.Kind).ToList();
        if (ordered.Count == 0) return string.Empty;

        var sb = new StringBuilder(capacity: 1024);
        // XACT_ABORT ON makes most runtime errors auto-rollback even
        // without an explicit CATCH, but we keep the TRY/CATCH as
        // belt-and-braces — some errors don't fire XACT_ABORT, and we
        // want a uniform "raise the original error to the caller" shape.
        sb.AppendLine("SET XACT_ABORT ON;");
        sb.AppendLine("BEGIN TRY");
        sb.Append("    BEGIN TRANSACTION SyncTableAlter_").Append(SafeName(table.Schema)).Append('_').Append(SafeName(table.Name)).AppendLine(";");
        sb.AppendLine();

        foreach (var step in ordered)
        {
            sb.Append("    -- ").AppendLine(step.Summary);
            // Indent each step so it reads nicely inside the TRY block;
            // SQL doesn't care about whitespace but logs / previews do.
            foreach (var line in step.Sql.Split('\n'))
                sb.Append("    ").AppendLine(line.TrimEnd('\r'));
            sb.AppendLine();
        }

        sb.Append("    COMMIT TRANSACTION SyncTableAlter_").Append(SafeName(table.Schema)).Append('_').Append(SafeName(table.Name)).AppendLine(";");
        sb.AppendLine("END TRY");
        sb.AppendLine("BEGIN CATCH");
        sb.AppendLine("    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;");
        // THROW preserves the original error number / message / line so
        // SqlException at the caller is identical to a non-wrapped run.
        sb.AppendLine("    THROW;");
        sb.AppendLine("END CATCH;");

        return sb.ToString();
    }

    /// <summary>
    /// Sanitises a SQL identifier for use as part of a savepoint /
    /// transaction name (alphanumerics + underscore only; max 32 chars
    /// segment). Transaction names tolerate up to 32 chars total in SQL
    /// Server — our schema_name pattern stays well under once both
    /// halves are joined with an underscore.
    /// </summary>
    private static string SafeName(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return "x";
        var sb = new StringBuilder(identifier.Length);
        foreach (var ch in identifier)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
        }
        var result = sb.Length == 0 ? "x" : sb.ToString();
        return result.Length > 12 ? result[..12] : result;
    }
}
