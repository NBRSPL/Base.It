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

    [Fact]
    public void AlignPair_ignore_whitespace_ignores_all_whitespace_including_literals()
    {
        // "Ignore spaces & tabs" means ALL spaces/tabs — including inside
        // string literals / identifiers. The visual toggle is intentionally
        // more aggressive than the in-sync hash so it truly hides every
        // whitespace-only difference.
        var (lit, _) = LineAligner.AlignPair("SET @x = 'a b'", "SET @x = 'a  b'", ignoreWhitespace: true);
        Assert.All(lit, l => Assert.Equal(LineState.Same, l.State));

        var (id, _) = LineAligner.AlignPair("SELECT [My Col]", "SELECT [MyCol]", ignoreWhitespace: true);
        Assert.All(id, l => Assert.Equal(LineState.Same, l.State));
    }

    [Fact]
    public void AlignPair_ignore_whitespace_handles_comment_with_apostrophe()
    {
        // Regression: an apostrophe in a comment must NOT be mistaken for a
        // string-literal start (which used to leave a whitespace-only comment
        // change wrongly marked Different / highlighted). A comment differing
        // only in whitespace is in sync when ignoring whitespace.
        var a = "-- the row's Quantity (out = -) SupplNo";
        var b = "-- the row's  Quantity (out = -)  SupplNo";
        var (lines, _) = LineAligner.AlignPair(a, b, ignoreWhitespace: true);
        Assert.All(lines, l => Assert.Equal(LineState.Same, l.State));
    }

    [Fact]
    public void AlignPair_ignore_whitespace_still_flags_real_content_change()
    {
        var (lines, _) = LineAligner.AlignPair("ReasonId INT;", "ReasonId INT, @X INT;", ignoreWhitespace: true);
        Assert.Contains(lines, l => l.State == LineState.Different);
    }
}
