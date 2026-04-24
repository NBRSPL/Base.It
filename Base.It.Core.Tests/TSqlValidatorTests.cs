using Base.It.Core.Parsing;
using Xunit;

namespace Base.It.Core.Tests;

public class TSqlValidatorTests
{
    [Fact]
    public void Valid_script_returns_ok()
    {
        var r = TSqlValidator.Validate("CREATE PROCEDURE dbo.Foo AS SELECT 1;");
        Assert.True(r.IsValid);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Invalid_script_returns_errors()
    {
        var r = TSqlValidator.Validate("CREATE PROCEDUREEE dbo.Foo AS SELECT 1;");
        Assert.False(r.IsValid);
        Assert.NotEmpty(r.Errors);
    }

    [Fact]
    public void Empty_input_is_valid()
    {
        Assert.True(TSqlValidator.Validate("").IsValid);
        Assert.True(TSqlValidator.Validate(null!).IsValid);
    }

    [Fact]
    public void Multi_batch_script_with_GO_parses()
    {
        var script = "CREATE TABLE dbo.A (Id INT);\nGO\nCREATE PROCEDURE dbo.P AS SELECT 1;\nGO";
        Assert.True(TSqlValidator.Validate(script).IsValid);
    }
}
