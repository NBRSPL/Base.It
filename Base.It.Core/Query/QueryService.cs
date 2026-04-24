using System.Data;
using Microsoft.Data.SqlClient;

namespace Base.It.Core.Query;

public sealed record QueryOutcome(bool IsResultSet, DataTable? Rows, int RowsAffected, string? Error);

/// <summary>
/// Executes arbitrary T-SQL. Returns either a DataTable (for SELECT-like
/// statements) or rows-affected (for DML/DDL). Errors are captured, not thrown.
/// </summary>
public sealed class QueryService
{
    public async Task<QueryOutcome> ExecuteAsync(
        string connectionString, string sql, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return new QueryOutcome(false, null, 0, "No connection string provided.");
        if (string.IsNullOrWhiteSpace(sql))
            return new QueryOutcome(false, null, 0, "Empty query.");

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };

            // Try a reader first so SELECTs get full rows back. If the reader
            // has no fields (pure DML/DDL), fall back to RecordsAffected.
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (reader.FieldCount == 0)
                return new QueryOutcome(false, null, reader.RecordsAffected, null);

            var table = new DataTable();
            table.Load(reader);
            return new QueryOutcome(true, table, table.Rows.Count, null);
        }
        catch (SqlException ex) { return new QueryOutcome(false, null, 0, $"SQL Error: {ex.Message}"); }
        catch (Exception ex)    { return new QueryOutcome(false, null, 0, $"Error: {ex.Message}"); }
    }
}
