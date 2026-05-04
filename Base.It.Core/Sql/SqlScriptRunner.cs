using System.Text;
using Microsoft.Data.SqlClient;

namespace Base.It.Core.Sql;

public enum ScriptStatus { Success, Failed }

public sealed record ScriptOutcome(
    ScriptStatus Status,
    int          BatchesExecuted,
    int          RowsAffectedTotal,
    string?      Error);

/// <summary>
/// Executes a SQL script (.sql file or string) against a connection,
/// splitting on the <c>GO</c> batch terminator the way SQL Server's
/// own tooling does. Each batch runs in its own <see cref="SqlCommand"/>
/// — the connection is shared but transactions are per-batch (T-SQL
/// CREATE/ALTER scripts typically don't wrap the whole script in a
/// transaction). On the first batch failure, execution stops and the
/// outcome reports the partial count.
///
/// Splitting rules (mirrors SSMS / sqlcmd, not the C# SQL grammar):
///   - <c>GO</c> is recognised only when it stands alone on a line
///     (after trimming whitespace), case-insensitive. Anything inside
///     a string literal or comment is left alone.
///   - A trailing optional integer (e.g. <c>GO 3</c>) is currently
///     ignored — we treat it as a plain batch terminator. SSDT-style
///     scripts rarely use the repeat form.
///   - Empty batches (whitespace between two <c>GO</c>s) are skipped
///     so they don't trigger the SqlCommand "empty batch" error.
/// </summary>
public sealed class SqlScriptRunner
{
    private readonly int _commandTimeoutSeconds;

    public SqlScriptRunner(int commandTimeoutSeconds = 120)
    {
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    public Task<ScriptOutcome> ExecuteFileAsync(
        string filePath, string connectionString, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return Task.FromResult(new ScriptOutcome(ScriptStatus.Failed, 0, 0, $"File not found: {filePath}"));
        var sql = File.ReadAllText(filePath);
        return ExecuteAsync(sql, connectionString, ct);
    }

    public async Task<ScriptOutcome> ExecuteAsync(
        string sql, string connectionString, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return new ScriptOutcome(ScriptStatus.Failed, 0, 0, "No connection string.");
        if (string.IsNullOrWhiteSpace(sql))
            return new ScriptOutcome(ScriptStatus.Failed, 0, 0, "Empty script.");

        var batches = SplitBatches(sql);
        if (batches.Count == 0)
            return new ScriptOutcome(ScriptStatus.Failed, 0, 0, "No executable batches found.");

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            int rowsTotal = 0;
            int executed  = 0;
            foreach (var batch in batches)
            {
                ct.ThrowIfCancellationRequested();
                await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = _commandTimeoutSeconds };
                var rows = await cmd.ExecuteNonQueryAsync(ct);
                if (rows > 0) rowsTotal += rows;
                executed++;
            }
            return new ScriptOutcome(ScriptStatus.Success, executed, rowsTotal, null);
        }
        catch (SqlException ex)
        {
            return new ScriptOutcome(ScriptStatus.Failed, 0, 0, $"SQL Error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ScriptOutcome(ScriptStatus.Failed, 0, 0, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Splits a SQL script into batches at every line that consists of
    /// just <c>GO</c> (case-insensitive, surrounding whitespace allowed).
    /// Anything inside a string literal (<c>'…'</c>) or a block comment
    /// (<c>/* … */</c>) is ignored — a stray <c>GO</c> in a comment
    /// won't accidentally split a batch.
    /// </summary>
    public static IReadOnlyList<string> SplitBatches(string sql)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        int i = 0;
        bool atLineStart = true;
        bool inString    = false;
        bool inLineComment = false;
        bool inBlockComment = false;

        while (i < sql.Length)
        {
            char c = sql[i];

            // Newline resets line-start + line-comment state regardless of context.
            if (c == '\r' || c == '\n')
            {
                current.Append(c);
                atLineStart = true;
                inLineComment = false;
                i++;
                continue;
            }

            if (inLineComment)
            {
                current.Append(c);
                i++;
                continue;
            }

            if (inBlockComment)
            {
                current.Append(c);
                if (c == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
                {
                    current.Append('/');
                    i += 2;
                    inBlockComment = false;
                    continue;
                }
                i++;
                continue;
            }

            if (inString)
            {
                current.Append(c);
                if (c == '\'')
                {
                    // Doubled-up '' is an escaped quote, not the end of the literal.
                    if (i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        current.Append('\'');
                        i += 2;
                        continue;
                    }
                    inString = false;
                }
                i++;
                continue;
            }

            // Not in a literal/comment.
            if (c == '\'')             { current.Append(c); inString    = true; atLineStart = false; i++; continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                                       { current.Append("--"); inLineComment = true; atLineStart = false; i += 2; continue; }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                                       { current.Append("/*"); inBlockComment = true; atLineStart = false; i += 2; continue; }

            // Whitespace (other than newline) keeps line-start state alive.
            if (c == ' ' || c == '\t')
            {
                current.Append(c);
                i++;
                continue;
            }

            // Possible "GO" at line start. Match case-insensitively, then
            // require the rest of the line to be whitespace (optionally
            // followed by an integer count we ignore for now).
            if (atLineStart && (c == 'G' || c == 'g')
                && i + 1 < sql.Length && (sql[i + 1] == 'O' || sql[i + 1] == 'o'))
            {
                int j = i + 2;
                // After GO, allow whitespace and optional digits, then EOL/EOF.
                while (j < sql.Length && (sql[j] == ' ' || sql[j] == '\t')) j++;
                while (j < sql.Length && sql[j] >= '0' && sql[j] <= '9')   j++;
                while (j < sql.Length && (sql[j] == ' ' || sql[j] == '\t')) j++;
                if (j == sql.Length || sql[j] == '\r' || sql[j] == '\n')
                {
                    // Confirmed batch terminator. Push the current batch.
                    var batch = current.ToString().Trim();
                    if (batch.Length > 0) result.Add(batch);
                    current.Clear();
                    // Skip past the GO line including its newline.
                    i = j;
                    if (i < sql.Length && sql[i] == '\r') i++;
                    if (i < sql.Length && sql[i] == '\n') i++;
                    atLineStart = true;
                    continue;
                }
            }

            current.Append(c);
            atLineStart = false;
            i++;
        }

        var tail = current.ToString().Trim();
        if (tail.Length > 0) result.Add(tail);
        return result;
    }
}
