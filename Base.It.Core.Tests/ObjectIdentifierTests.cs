using Base.It.Core.Models;
using Xunit;

namespace Base.It.Core.Tests;

public class ObjectIdentifierTests
{
    [Theory]
    [InlineData("Foo",           "dbo", "Foo")]
    [InlineData("dbo.Foo",       "dbo", "Foo")]
    [InlineData("sales.Orders",  "sales", "Orders")]
    [InlineData("[dbo].[Foo]",   "dbo", "Foo")]
    [InlineData("  Foo  ",       "dbo", "Foo")]
    public void Parses_qualified_and_unqualified_names(string input, string schema, string name)
    {
        var id = ObjectIdentifier.Parse(input);
        Assert.Equal(schema, id.Schema);
        Assert.Equal(name, id.Name);
    }

    [Fact]
    public void Empty_input_throws()
    {
        Assert.Throws<ArgumentException>(() => ObjectIdentifier.Parse(""));
        Assert.Throws<ArgumentException>(() => ObjectIdentifier.Parse("   "));
    }

    [Fact]
    public void ToString_emits_bracketed_two_part_name()
    {
        Assert.Equal("[dbo].[Foo]", new ObjectIdentifier("dbo", "Foo").ToString());
    }
}
