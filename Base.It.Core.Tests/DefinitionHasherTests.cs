using Base.It.Core.Hashing;
using Xunit;

namespace Base.It.Core.Tests;

public class DefinitionHasherTests
{
    [Fact]
    public void Empty_input_returns_empty_hash()
    {
        Assert.Equal(string.Empty, DefinitionHasher.Hash(""));
        Assert.Equal(string.Empty, DefinitionHasher.Hash(null!));
    }

    [Fact]
    public void Hash_is_stable_for_same_input()
    {
        var a = DefinitionHasher.Hash("CREATE PROCEDURE dbo.Foo AS SELECT 1");
        var b = DefinitionHasher.Hash("CREATE PROCEDURE dbo.Foo AS SELECT 1");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length); // SHA-256 hex
    }

    [Fact]
    public void Hash_is_lineending_insensitive()
    {
        var crlf = DefinitionHasher.Hash("CREATE PROC X\r\nAS SELECT 1\r\n");
        var lf   = DefinitionHasher.Hash("CREATE PROC X\nAS SELECT 1\n");
        var cr   = DefinitionHasher.Hash("CREATE PROC X\rAS SELECT 1\r");
        Assert.Equal(crlf, lf);
        Assert.Equal(lf, cr);
    }

    [Fact]
    public void Hash_ignores_trailing_whitespace_per_line()
    {
        var clean   = DefinitionHasher.Hash("CREATE PROC X\nAS SELECT 1\n");
        var trailed = DefinitionHasher.Hash("CREATE PROC X   \nAS SELECT 1\t\n");
        Assert.Equal(clean, trailed);
    }

    [Fact]
    public void Hash_ignores_indentation_and_blank_lines()
    {
        // Same logic, different layout — the commonest false-positive on
        // real diffs. Must hash equal now that the hash uses SqlFormatter.
        var tight = DefinitionHasher.Hash("CREATE PROC X AS BEGIN SELECT 1 END");
        var airy  = DefinitionHasher.Hash("CREATE PROC X AS\n\nBEGIN\n\n    SELECT 1\n\nEND");
        Assert.Equal(tight, airy);
    }

    [Fact]
    public void Hash_ignores_keyword_casing()
    {
        // SQL Server keywords are case-insensitive; re-saving a proc with a
        // formatter that re-cases keywords is not a schema change. The old
        // hasher treated this as a difference — that was the bug.
        var upper = DefinitionHasher.Hash("SELECT Col FROM dbo.T");
        var lower = DefinitionHasher.Hash("select Col from dbo.T");
        Assert.Equal(upper, lower);
    }

    [Fact]
    public void Hash_preserves_string_literal_content()
    {
        // Inside quotes is DATA — casing and spacing there are significant.
        Assert.NotEqual(
            DefinitionHasher.Hash("SELECT 'Hello'"),
            DefinitionHasher.Hash("SELECT 'hello'"));
        Assert.NotEqual(
            DefinitionHasher.Hash("SELECT 'a b'"),
            DefinitionHasher.Hash("SELECT 'a  b'"));
    }

    [Fact]
    public void Hash_still_detects_real_content_differences()
    {
        Assert.NotEqual(
            DefinitionHasher.Hash("SELECT 1"),
            DefinitionHasher.Hash("SELECT 2"));
        Assert.NotEqual(
            DefinitionHasher.Hash("CREATE PROC P AS SELECT A FROM T"),
            DefinitionHasher.Hash("CREATE PROC P AS SELECT A, B FROM T"));
    }

    [Fact]
    public void Unparseable_input_still_hashes_deterministically()
    {
        // Falls back to raw line-normalisation — same garbage in, same hash.
        var a = DefinitionHasher.Hash("this is not ((( valid T-SQL '''");
        var b = DefinitionHasher.Hash("this is not ((( valid T-SQL '''");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var once  = DefinitionHasher.Normalize("create   proc X\r\nas  select 1\n\n");
        var twice = DefinitionHasher.Normalize(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Hash_ignores_tabs_vs_spaces_indentation()
    {
        // "Ignore tab and space": the same body indented with tabs vs
        // spaces (and different amounts) must be in-sync.
        var spaces = DefinitionHasher.Hash("CREATE PROC X AS\nBEGIN\n    SELECT 1\nEND");
        var tabs   = DefinitionHasher.Hash("CREATE PROC X AS\nBEGIN\n\t\tSELECT 1\nEND");
        Assert.Equal(spaces, tabs);
    }

    [Fact]
    public void Hash_ignores_whitespace_between_tokens()
    {
        // Extra spaces / tabs around operators and commas are formatting.
        var tight = DefinitionHasher.Hash("SELECT a,b FROM t WHERE x=1");
        var loose = DefinitionHasher.Hash("SELECT a ,  b\tFROM  t\nWHERE x  =  1");
        Assert.Equal(tight, loose);
    }

    [Fact]
    public void Hash_ignores_comment_internal_whitespace()
    {
        // A comment's alignment / trailing spaces are formatting, not
        // content — same words, different spacing → in-sync.
        var a = DefinitionHasher.Hash("SELECT 1 -- keep in sync");
        var b = DefinitionHasher.Hash("SELECT 1 --    keep   in   sync   ");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Hash_treats_different_comment_text_as_different()
    {
        // Comment TEXT is content: a genuinely different comment is a real
        // (if minor) difference and must NOT be hidden as in-sync.
        Assert.NotEqual(
            DefinitionHasher.Hash("SELECT 1 -- alpha"),
            DefinitionHasher.Hash("SELECT 1 -- beta"));
    }

    [Fact]
    public void Hash_preserves_quoted_identifier_content()
    {
        // A column literally named [My Col] is NOT the same object as one
        // named [MyCol] — whitespace inside a quoted identifier is data.
        Assert.NotEqual(
            DefinitionHasher.Hash("SELECT [My Col] FROM t"),
            DefinitionHasher.Hash("SELECT [MyCol] FROM t"));
    }
}
