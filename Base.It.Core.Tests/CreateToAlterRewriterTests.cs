using Base.It.Core.Models;
using Base.It.Core.Sync;
using Xunit;

namespace Base.It.Core.Tests;

public class CreateToAlterRewriterTests
{
    [Fact]
    public void Rewrites_procedure_case_insensitively()
    {
        var r = CreateToAlterRewriter.Rewrite(
            "create   procedure dbo.Foo as select 1",
            SqlObjectType.StoredProcedure);
        Assert.StartsWith("ALTER PROCEDURE", r, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlObjectType.View,               "CREATE VIEW dbo.v AS SELECT 1",     "ALTER VIEW")]
    [InlineData(SqlObjectType.Trigger,            "CREATE TRIGGER t ON dbo.A AFTER INSERT AS RETURN", "ALTER TRIGGER")]
    [InlineData(SqlObjectType.ScalarFunction,     "CREATE FUNCTION dbo.f() RETURNS INT AS BEGIN RETURN 1 END", "ALTER FUNCTION")]
    [InlineData(SqlObjectType.InlineTableFunction,"CREATE FUNCTION dbo.f() RETURNS TABLE AS RETURN SELECT 1 X", "ALTER FUNCTION")]
    public void Rewrites_all_module_types(SqlObjectType type, string input, string expectedPrefix)
    {
        var r = CreateToAlterRewriter.Rewrite(input, type);
        Assert.StartsWith(expectedPrefix, r, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Leaves_table_definition_unchanged()
    {
        var input = "CREATE TABLE dbo.T (Id INT);";
        Assert.Equal(input, CreateToAlterRewriter.Rewrite(input, SqlObjectType.Table));
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Equal("", CreateToAlterRewriter.Rewrite("",   SqlObjectType.StoredProcedure));
        Assert.Null(CreateToAlterRewriter.Rewrite(null!, SqlObjectType.StoredProcedure));
    }

    [Fact]
    public void Replaces_only_the_first_occurrence()
    {
        var input = "CREATE PROCEDURE dbo.Foo AS\nSELECT 'CREATE PROCEDURE something' FROM x";
        var r = CreateToAlterRewriter.Rewrite(input, SqlObjectType.StoredProcedure);
        Assert.StartsWith("ALTER PROCEDURE", r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'CREATE PROCEDURE something'", r);
    }
}
