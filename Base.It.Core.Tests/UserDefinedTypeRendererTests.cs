using Base.It.Core.Config;
using Base.It.Core.Dacpac;
using Base.It.Core.Models;
using Base.It.Core.Sql;
using Base.It.Core.Sync;
using Xunit;

namespace Base.It.Core.Tests;

/// <summary>
/// Coverage for user-defined type support added in 1.3.1 — the enum
/// values, the renderers, and the downstream registrations
/// (WatchGroup default types, DacpacExporter folder mapping,
/// CreateToAlterRewriter's no-touch policy).
///
/// The live-catalog reads (SqlObjectScripter) require a real SQL
/// connection and are exercised only by manual/integration testing;
/// they're not covered here.
/// </summary>
public class UserDefinedTypeRendererTests
{
    [Fact]
    public void Enum_includes_the_two_new_type_values()
    {
        Assert.True(Enum.IsDefined(typeof(SqlObjectType), SqlObjectType.TableType));
        Assert.True(Enum.IsDefined(typeof(SqlObjectType), SqlObjectType.UserDefinedDataType));
    }

    // ─── Table-type rendering ────────────────────────────────────────────

    [Fact]
    public void Table_type_renders_as_create_type_with_columns()
    {
        var cols = new[]
        {
            SimpleColumn("Id",   "int",     precision: 10, scale: 0, isNullable: false),
            SimpleColumn("Name", "nvarchar", maxLength: 200, isNullable: true),
        };
        var script = TableScriptRenderer.RenderTableType(
            schema: "dbo", name: "OrderRows",
            columns: cols,
            keyConstraints:   Array.Empty<TableScriptRenderer.KeyConstraintGroup>(),
            checkConstraints: Array.Empty<TableScriptRenderer.CheckConstraintInfo>(),
            dbCollation: null);

        Assert.Contains("CREATE TYPE [dbo].[OrderRows] AS TABLE", script);
        Assert.Contains("[Id]",   script);
        Assert.Contains("[Name]", script);
        Assert.Contains("NOT NULL", script);
        Assert.EndsWith("GO\n", script);
    }

    [Fact]
    public void Table_type_includes_inline_primary_key()
    {
        var cols = new[] { SimpleColumn("Id", "int", precision: 10, scale: 0, isNullable: false) };
        var pk = new TableScriptRenderer.KeyConstraintGroup(
            Name: "PK_OrderRows", Type: "PK",
            IndexType: "CLUSTERED", FillFactor: 0, IsPadded: false,
            DataSpaceName: "PRIMARY",
            Columns: new List<(string, bool)> { ("Id", false) });

        var script = TableScriptRenderer.RenderTableType(
            schema: "dbo", name: "OrderRows",
            columns: cols,
            keyConstraints: new[] { pk },
            checkConstraints: Array.Empty<TableScriptRenderer.CheckConstraintInfo>(),
            dbCollation: null);

        Assert.Contains("PRIMARY KEY", script);
        Assert.Contains("[PK_OrderRows]", script);
    }

    // ─── Alias / user-defined data type rendering ────────────────────────

    [Fact]
    public void Alias_type_renders_from_base_type_with_precision_and_scale()
    {
        // CREATE TYPE [dbo].[Money2] FROM DECIMAL(19,4) NOT NULL
        var script = TableScriptRenderer.RenderUserDefinedDataType(
            schema: "dbo", name: "Money2",
            baseTypeName: "decimal",
            maxLength: 9, precision: 19, scale: 4, isNullable: false);

        Assert.Contains("CREATE TYPE [dbo].[Money2]", script);
        Assert.Contains("FROM DECIMAL(19,4)",         script);
        Assert.Contains("NOT NULL",                   script);
        Assert.EndsWith("GO\n",                       script);
    }

    [Fact]
    public void Alias_type_renders_from_string_base_with_length_and_null()
    {
        // NVARCHAR max_length is bytes / 2 (per SqlObjectScripter.RenderTypeSpec).
        // For NVARCHAR(255), max_length = 510.
        var script = TableScriptRenderer.RenderUserDefinedDataType(
            schema: "dbo", name: "EmailAddress",
            baseTypeName: "nvarchar",
            maxLength: 510, precision: 0, scale: 0, isNullable: true);

        Assert.Contains("FROM NVARCHAR (255)", script);
        Assert.Contains("NULL",                 script);
        Assert.DoesNotContain("NOT NULL",       script);
    }

    // ─── Downstream registrations ────────────────────────────────────────

    [Fact]
    public void WatchGroup_default_types_include_the_new_types()
    {
        Assert.Contains(SqlObjectType.TableType,           WatchGroup.AllUserTypes);
        Assert.Contains(SqlObjectType.UserDefinedDataType, WatchGroup.AllUserTypes);
    }

    [Fact]
    public void DacpacExporter_maps_types_to_Types_folder()
    {
        Assert.Equal("Types",  DacpacExporter.TypeFolder(SqlObjectType.TableType));
        Assert.Equal("Types",  DacpacExporter.TypeFolder(SqlObjectType.UserDefinedDataType));
        Assert.Equal("Types2", DacpacExporter.NewTypeFolder(SqlObjectType.TableType));
        Assert.Equal("Types2", DacpacExporter.NewTypeFolder(SqlObjectType.UserDefinedDataType));
    }

    [Fact]
    public void CreateToAlterRewriter_leaves_user_defined_types_unchanged()
    {
        // Types can't be ALTER'd in SQL Server. The rewriter must return
        // the definition verbatim so the caller can detect the "type +
        // target exists" case and refuse the sync cleanly (SyncService
        // handles that guard).
        var tt   = "CREATE TYPE [dbo].[OrderRows] AS TABLE ([Id] INT NOT NULL);";
        var uddt = "CREATE TYPE [dbo].[Money2] FROM DECIMAL(19,4) NOT NULL;";
        Assert.Equal(tt,   CreateToAlterRewriter.Rewrite(tt,   SqlObjectType.TableType));
        Assert.Equal(uddt, CreateToAlterRewriter.Rewrite(uddt, SqlObjectType.UserDefinedDataType));
    }

    private static TableScriptRenderer.ColumnInfo SimpleColumn(
        string name, string typeName,
        int  maxLength   = 0,
        byte precision   = 0,
        byte scale       = 0,
        bool isNullable  = true) =>
        new(Name: name,
            TypeName: typeName,
            MaxLength: (short)maxLength,
            Precision: precision,
            Scale: scale,
            IsNullable: isNullable,
            IsIdentity: false,
            IdentitySeed: null,
            IdentityIncrement: null,
            IdentityNotForReplication: false,
            ComputedDefinition: null,
            ComputedIsPersisted: null,
            DefaultName: null,
            DefaultDefinition: null,
            CollationName: null,
            IsRowGuidCol: false);
}
