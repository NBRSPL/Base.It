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
}
