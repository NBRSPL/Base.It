using Base.It.Core.Diff;
using Xunit;

namespace Base.It.Core.Tests;

public class LineAlignerIgnoreWhitespaceTests
{
    [Fact]
    public void AlignPair_whitespace_only_difference_is_Same_when_ignored()
    {
        var a = "SELECT id\n    FROM t";
        var b = "SELECT id\nFROM t";           // differs only by indentation

        var (noIgnore, _) = LineAligner.AlignPair(a, b, ignoreWhitespace: false);
        Assert.Contains(noIgnore, l => l.State == LineState.Different);

        var (ignore, _) = LineAligner.AlignPair(a, b, ignoreWhitespace: true);
        Assert.All(ignore, l => Assert.Equal(LineState.Same, l.State));
    }

    [Fact]
    public void AlignPair_real_change_still_differs_when_ignoring_whitespace()
    {
        var a = "SELECT id FROM t";
        var b = "SELECT id, name FROM t";       // genuine content change

        var (ignore, _) = LineAligner.AlignPair(a, b, ignoreWhitespace: true);
        Assert.Contains(ignore, l => l.State == LineState.Different);
    }

    [Fact]
    public void Align_whitespace_only_difference_is_Same_when_ignored()
    {
        var self  = "SELECT a\n\tFROM t";       // tab indent
        var peer  = "SELECT a\nFROM t";

        var noIgnore = LineAligner.Align(self, new[] { peer }, ignoreWhitespace: false);
        Assert.Contains(noIgnore, l => l.State == LineState.Different);

        var ignore = LineAligner.Align(self, new[] { peer }, ignoreWhitespace: true);
        Assert.All(ignore, l => Assert.Equal(LineState.Same, l.State));
    }

    [Fact]
    public void Ignoring_whitespace_does_not_change_displayed_text()
    {
        var a = "SELECT id\n    FROM t";
        var (lines, _) = LineAligner.AlignPair(a, "SELECT id\nFROM t", ignoreWhitespace: true);
        // The indentation is preserved in what the user sees; only the
        // comparison ignores it.
        Assert.Contains(lines, l => l.Text == "    FROM t");
    }
}
