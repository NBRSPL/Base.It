using Base.It.Core.Batch;
using Xunit;

namespace Base.It.Core.Tests;

public class ObjectListLoaderTests
{
    [Fact]
    public void Csv_with_header_and_column_name_returns_names()
    {
        var lines = new[]
        {
            "Object name,Comment",
            "usp_Foo,something",
            "vw_Bar,\"note, with comma\"",
            ""
        };
        var names = ObjectListLoader.FromCsvLines(lines);
        Assert.Equal(new[] { "usp_Foo", "vw_Bar" }, names);
    }

    [Fact]
    public void Csv_missing_header_column_returns_empty()
    {
        var lines = new[] { "Name,Comment", "usp_Foo,something" };
        Assert.Empty(ObjectListLoader.FromCsvLines(lines));
    }

    [Fact]
    public void Csv_dedupes_names_case_insensitively()
    {
        var lines = new[]
        {
            "Object name",
            "usp_Foo",
            "USP_FOO",
            "vw_Bar"
        };
        var names = ObjectListLoader.FromCsvLines(lines);
        Assert.Equal(new[] { "usp_Foo", "vw_Bar" }, names);
    }

    [Fact]
    public void Unsupported_file_returns_empty()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"baseit_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tmp, "Object name\nusp_Foo\n");
        try { Assert.Empty(ObjectListLoader.FromFile(tmp)); }
        finally { File.Delete(tmp); }
    }
}
