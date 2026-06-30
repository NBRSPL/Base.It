using Base.It.Core.Parsing;
using Xunit;

namespace Base.It.Core.Tests;

public class SqlFormatterTests
{
    [Fact]
    public void Whitespace_and_casing_differences_canonicalise_to_the_same_text()
    {
        // Same query, wildly different formatting + keyword casing.
        var a = "select id,name from dbo.Customers where id=@id";
        var b = "SELECT   id ,\n   name\nFROM dbo.Customers\n   WHERE   id = @id";

        Assert.True(SqlFormatter.TryFormat(a, out var fa));
        Assert.True(SqlFormatter.TryFormat(b, out var fb));

        // After formatting both sides, the only-cosmetic difference is gone.
        Assert.Equal(fa, fb);
    }

    [Fact]
    public void Real_changes_survive_formatting()
    {
        var a = "SELECT id FROM dbo.Customers WHERE id = @id";
        var b = "SELECT id, name FROM dbo.Customers WHERE id = @id"; // extra column

        var fa = SqlFormatter.Format(a);
        var fb = SqlFormatter.Format(b);

        Assert.NotEqual(fa, fb);
    }

    [Fact]
    public void Keywords_are_uppercased()
    {
        var formatted = SqlFormatter.Format("select 1 as x");
        Assert.Contains("SELECT", formatted);
    }

    [Fact]
    public void Unparseable_input_is_returned_unchanged()
    {
        var garbage = "this is not ::: valid sql @@@ (";
        Assert.False(SqlFormatter.TryFormat(garbage, out var echoed));
        Assert.Equal(garbage, echoed);
        Assert.Equal(garbage, SqlFormatter.Format(garbage));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_input_is_safe(string? input)
    {
        Assert.False(SqlFormatter.TryFormat(input, out _));
        // Format never throws and never returns null.
        Assert.NotNull(SqlFormatter.Format(input));
    }

    [Fact]
    public void Create_procedure_definitions_are_formattable()
    {
        var proc = "create procedure dbo.usp_Get @id int as select * from dbo.Orders o where o.Id=@id";
        Assert.True(SqlFormatter.TryFormat(proc, out var formatted));
        Assert.Contains("CREATE", formatted);
        Assert.Contains("SELECT", formatted);
    }
}
