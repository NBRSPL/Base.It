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
    // SQL Server allows $, _, #, @ and digits in regular identifiers — the
    // parser must pass any of these through unchanged. These names come
    // from the wild (user-generated procs / tables) and are not the
    // tool's choice to reject.
    [InlineData("sp_mig_test_nishant_$$$_1",       "dbo", "sp_mig_test_nishant_$$$_1")]
    [InlineData("dbo.sp_mig_test_nishant_$$$_1",   "dbo", "sp_mig_test_nishant_$$$_1")]
    [InlineData("[dbo].[sp_mig_test_nishant_$$$_1]", "dbo", "sp_mig_test_nishant_$$$_1")]
    [InlineData("schema_with_under.proc_name",     "schema_with_under", "proc_name")]
    [InlineData("dbo.Order#123",                   "dbo", "Order#123")]
    [InlineData("dbo.AccountAlias",                "dbo", "AccountAlias")]
    [InlineData("dbo.Some.Object",                 "dbo", "Some.Object")] // only first dot splits schema/name
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
