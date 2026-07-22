using Base.It.Core.Schema;
using Xunit;

namespace Base.It.Core.Tests;

/// <summary>
/// Non-DB contract tests for the table-type / alias-type snapshot path.
/// The SQL itself needs a live server (covered by manual smoke), but the
/// empty-input short-circuit must never touch a connection.
/// </summary>
public class BulkSchemaFetcherTypeTests
{
    [Fact]
    public async Task FetchTypeDefinitions_returns_empty_without_touching_a_connection()
    {
        // Empty type set → no work, no connection opened. A bogus
        // connection string proves we never dial out on the empty path.
        var result = await BulkSchemaFetcher.FetchTypeDefinitionsAsync(
            connectionString: "Server=nonexistent;Database=none;Connect Timeout=1;",
            typeMetas: System.Array.Empty<BulkSchemaFetcher.ObjectMetadata>(),
            onRow: null,
            ct: default);

        Assert.Empty(result);
    }

    [Fact]
    public void ObjectMetadata_modify_date_is_nullable_for_alias_types()
    {
        // UDDTs carry no modify_date — the record must accept null so the
        // 3-way metadata UNION can represent them.
        var m = new BulkSchemaFetcher.ObjectMetadata(
            ObjectId: -42, Schema: "dbo", Name: "Money2",
            Kind: Base.It.Core.Models.SqlObjectType.UserDefinedDataType,
            ModifyDateUtc: null);
        Assert.Null(m.ModifyDateUtc);
        Assert.Equal(Base.It.Core.Models.SqlObjectType.UserDefinedDataType, m.Kind);
    }
}
