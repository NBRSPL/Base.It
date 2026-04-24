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
        var clean  = DefinitionHasher.Hash("CREATE PROC X\nAS SELECT 1\n");
        var trailed = DefinitionHasher.Hash("CREATE PROC X   \nAS SELECT 1\t\n");
        Assert.Equal(clean, trailed);
    }

    [Fact]
    public void Hash_is_case_sensitive_on_definition()
    {
        // SQL is case-insensitive but our hash is literal — two servers returning
        // identical casing will match. Case-folding would cause false "in sync"
        // reports when a proc was re-saved with different casing.
        var upper = DefinitionHasher.Hash("SELECT 1");
        var lower = DefinitionHasher.Hash("select 1");
        Assert.NotEqual(upper, lower);
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var once  = DefinitionHasher.Normalize("a  \r\nb\n\n");
        var twice = DefinitionHasher.Normalize(once);
        Assert.Equal(once, twice);
    }
}
