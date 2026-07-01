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

    // ─── Comment preservation ────────────────────────────────────────────
    // ScriptDom's ScriptGenerator throws comments away when it round-trips
    // through the AST — comments aren't AST nodes. The token-stream
    // formatter we ship avoids that path so comments survive.

    [Fact]
    public void Single_line_comment_is_preserved()
    {
        var sql = "SELECT id FROM t -- lookup by id\n";
        var formatted = SqlFormatter.Format(sql);
        Assert.Contains("-- lookup by id", formatted);
    }

    [Fact]
    public void Block_comment_is_preserved()
    {
        var sql = "SELECT /* the id */ id FROM t";
        var formatted = SqlFormatter.Format(sql);
        Assert.Contains("/* the id */", formatted);
    }

    [Fact]
    public void Header_comment_stays_on_its_own_line_above_statement()
    {
        var sql = "-- header comment\nSELECT 1";
        var formatted = SqlFormatter.Format(sql);
        Assert.Contains("-- header comment", formatted);
        // Ordering: header appears before SELECT
        Assert.True(formatted.IndexOf("-- header comment") < formatted.IndexOf("SELECT"));
    }

    [Fact]
    public void Trailing_line_comment_stays_inline_with_prev_content()
    {
        var sql = "SELECT id FROM t -- inline";
        var formatted = SqlFormatter.Format(sql);
        // The `-- inline` comment should be on the same output line as
        // some other visible content, not stranded on a fresh line.
        var line = formatted.Split('\n').First(l => l.Contains("-- inline"));
        Assert.Matches(@"\S.*-- inline", line);
    }

    [Fact]
    public void Multiline_block_comment_preserved_verbatim_across_lines()
    {
        var sql = "/* line 1\n   line 2 */\nSELECT 1";
        var formatted = SqlFormatter.Format(sql);
        Assert.Contains("line 1", formatted);
        Assert.Contains("line 2", formatted);
    }

    [Fact]
    public void Comments_survive_inside_a_proc_body()
    {
        // Realistic proc from the wild — header comment, block hint,
        // inline column comment. All must appear in the output.
        var proc = @"
CREATE PROCEDURE dbo.usp_Get
    @id INT
AS
BEGIN
    -- fetch order details
    /* NB: no locks */
    SELECT o.id, o.name -- pick these two
    FROM dbo.Orders o
    WHERE o.Id = @id
END";
        var formatted = SqlFormatter.Format(proc);
        Assert.Contains("-- fetch order details", formatted);
        Assert.Contains("/* NB: no locks */",     formatted);
        Assert.Contains("-- pick these two",      formatted);
    }

    // ─── Structural layout ───────────────────────────────────────────────

    [Fact]
    public void Begin_end_block_gets_indented()
    {
        var sql = "CREATE PROC dbo.p AS BEGIN SELECT 1 END";
        var formatted = SqlFormatter.Format(sql);
        // The SELECT inside BEGIN…END should be indented (4 spaces).
        var lines = formatted.Split('\n');
        var selectLine = lines.First(l => l.TrimStart().StartsWith("SELECT"));
        Assert.StartsWith("    ", selectLine);
    }

    [Fact]
    public void Each_clause_gets_its_own_line()
    {
        var sql = "SELECT id FROM t WHERE id = 1";
        var formatted = SqlFormatter.Format(sql).TrimEnd();
        var lines = formatted.Split('\n');
        Assert.Equal(3, lines.Length);           // SELECT / FROM / WHERE
        Assert.StartsWith("SELECT",       lines[0]);
        Assert.StartsWith("FROM",         lines[1]);
        Assert.StartsWith("WHERE",        lines[2]);
    }

    [Fact]
    public void Punctuation_gets_no_extra_leading_space()
    {
        var sql = "SELECT a , b , c FROM t";
        var formatted = SqlFormatter.Format(sql);
        // Comma has no leading space (only trailing) — matches the PR
        // author's original intent for the diff-canonical form.
        Assert.Contains("a, b, c", formatted);
        Assert.DoesNotContain(" ,",  formatted);
    }

    [Fact]
    public void Dot_qualified_names_stay_glued()
    {
        var sql = "SELECT * FROM dbo.Orders";
        var formatted = SqlFormatter.Format(sql);
        Assert.Contains("dbo.Orders", formatted);
        Assert.DoesNotContain("dbo .",   formatted);
        Assert.DoesNotContain(". Orders", formatted);
    }

    [Fact]
    public void Batch_separator_GO_gets_its_own_line_and_resets_indent()
    {
        var sql = "CREATE PROC dbo.p AS BEGIN SELECT 1 END\nGO\nSELECT 2";
        var formatted = SqlFormatter.Format(sql);
        var lines = formatted.Split('\n');
        // GO alone, no indent.
        var goLine = lines.First(l => l.Trim() == "GO");
        Assert.Equal("GO", goLine);
        // SELECT 2 is at indent 0 (batch reset).
        var secondSelect = lines.Last(l => l.Contains("SELECT 2"));
        Assert.StartsWith("SELECT",  secondSelect);
    }
}
