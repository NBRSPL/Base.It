using Base.It.Core.Diff;
using Xunit;

namespace Base.It.Core.Tests;

/// <summary>
/// Smoke tests for the two-pass diff (token LCS + char refinement).
/// Not exhaustive — just enough to flush out the obvious crashes and
/// the "whole line painted on a 1-char whitespace change" regression.
/// </summary>
public class CharDiffSmokeTests
{
    [Fact]
    public void Equal_strings_produce_one_equal_segment_each()
    {
        var (a, b) = CharDiff.Diff("CREATE PROC dbo.Foo AS SELECT 1", "CREATE PROC dbo.Foo AS SELECT 1");
        Assert.Single(a);
        Assert.Single(b);
        Assert.Equal(CharDiff.SegmentKind.Equal, a[0].Kind);
        Assert.Equal(CharDiff.SegmentKind.Equal, b[0].Kind);
    }

    [Fact]
    public void Trailing_space_only_highlights_the_extra_space()
    {
        var (a, b) = CharDiff.Diff("SELECT *", "SELECT * ");
        // A should be entirely Equal, B should end with one Added " ".
        Assert.All(a, s => Assert.Equal(CharDiff.SegmentKind.Equal, s.Kind));
        Assert.Equal(CharDiff.SegmentKind.Added, b[^1].Kind);
        Assert.Equal(" ", b[^1].Text);
    }

    [Fact]
    public void Leading_indent_change_highlights_only_the_extra_spaces()
    {
        var (a, _) = CharDiff.Diff("    SELECT", "  SELECT");
        // Two of the four leading spaces should remain Equal; the other
        // two should be Removed. "SELECT" stays Equal.
        var removedText = string.Concat(
            a.Where(s => s.Kind == CharDiff.SegmentKind.Removed).Select(s => s.Text));
        Assert.Equal("  ", removedText);
    }

    [Fact]
    public void Word_swap_highlights_only_the_word_pair()
    {
        var (a, b) = CharDiff.Diff("FROM Users", "FROM Customers");
        // "FROM " should survive as one Equal segment on both sides.
        Assert.Equal("FROM ", a[0].Text);
        Assert.Equal(CharDiff.SegmentKind.Equal, a[0].Kind);
        Assert.Equal("FROM ", b[0].Text);
        Assert.Equal(CharDiff.SegmentKind.Equal, b[0].Kind);
    }

    [Fact]
    public void Char_refinement_keeps_common_prefix_inside_word()
    {
        // "Customer" → "Customers" should highlight only the trailing "s".
        var (a, b) = CharDiff.Diff("[Customer]", "[Customers]");
        // The trailing "s" must appear as Added in B.
        Assert.Contains(b, s => s.Kind == CharDiff.SegmentKind.Added && s.Text == "s");
    }

    [Fact]
    public void All_different_short_strings_do_not_throw()
    {
        // Many short inputs that historically tripped the LCS walker
        // off-by-ones. Just check the call returns.
        CharDiff.Diff("x", "y");
        CharDiff.Diff("", "abc");
        CharDiff.Diff("abc", "");
        CharDiff.Diff("a", "");
        CharDiff.Diff("", "a");
        CharDiff.Diff("ab", "ba");
        CharDiff.Diff(" ", "  ");
    }

    [Fact]
    public void AlignPair_two_lines_all_different_pairs_index_by_index()
    {
        var a = "alpha\nbeta";
        var b = "gamma\ndelta";
        var (aLines, bLines) = LineAligner.AlignPair(a, b);
        // Every line should end up Different with Segments populated
        // (replace pairing), not as pure inserts/deletes.
        Assert.All(aLines, l => Assert.Equal(LineState.Different, l.State));
        Assert.All(bLines, l => Assert.Equal(LineState.Different, l.State));
        Assert.NotNull(aLines[0].Segments);
        Assert.NotNull(aLines[1].Segments);
        Assert.NotNull(bLines[0].Segments);
        Assert.NotNull(bLines[1].Segments);
    }

    [Fact]
    public void AlignPair_pure_insert_at_end_is_not_a_replace()
    {
        var a = "alpha\nbeta";
        var b = "alpha\nbeta\ngamma";
        var (aLines, bLines) = LineAligner.AlignPair(a, b);
        Assert.Equal(LineState.Same, aLines[0].State);
        Assert.Equal(LineState.Same, aLines[1].State);
        Assert.Equal(LineState.Same, bLines[0].State);
        Assert.Equal(LineState.Same, bLines[1].State);
        Assert.Equal(LineState.Different, bLines[2].State);
        // bLines[2] is a pure insert — no counterpart on A side.
        Assert.Equal(-1, bLines[2].PairIndex);
    }

    [Fact]
    public void AlignPair_block_rewrite_pairs_each_line()
    {
        var a = "x1\nx2\nx3\nend";
        var b = "y1\ny2\ny3\nend";
        var (aLines, bLines) = LineAligner.AlignPair(a, b);
        // Every changed line should have a Segments list (i.e. be a
        // Replace pair, not a pure delete/insert). The old zipper
        // only caught the first delete+insert pair; the new one
        // catches all three.
        Assert.NotNull(aLines[0].Segments);
        Assert.NotNull(aLines[1].Segments);
        Assert.NotNull(aLines[2].Segments);
        Assert.NotNull(bLines[0].Segments);
        Assert.NotNull(bLines[1].Segments);
        Assert.NotNull(bLines[2].Segments);
        // "end" should be Same on both sides.
        Assert.Equal(LineState.Same, aLines[3].State);
        Assert.Equal(LineState.Same, bLines[3].State);
    }

    [Fact]
    public void Case_only_change_highlights_only_the_case_differing_chars()
    {
        // "Foo" → "foo": only the first char differs (F vs f). The
        // char-level refinement pass inside the word identifies "oo" as
        // common and isolates the single-char case swap as the diff —
        // confirming the diff is MORE precise than just word-level.
        var (a, b) = CharDiff.Diff("SELECT Foo FROM bar", "SELECT foo FROM bar");
        var aRemoved = string.Concat(a.Where(s => s.Kind == CharDiff.SegmentKind.Removed).Select(s => s.Text));
        var bAdded   = string.Concat(b.Where(s => s.Kind == CharDiff.SegmentKind.Added)  .Select(s => s.Text));
        Assert.Equal("F", aRemoved);
        Assert.Equal("f", bAdded);
    }

    [Fact]
    public void Numeric_change_inside_word_highlights_only_changed_digits()
    {
        // "VARCHAR(50)" → "VARCHAR(100)" — char-level refinement should
        // hit the "5" → "10" inside the word, not paint the whole word.
        var (a, b) = CharDiff.Diff("VARCHAR(50)", "VARCHAR(100)");
        var aRemoved = string.Concat(a.Where(s => s.Kind == CharDiff.SegmentKind.Removed).Select(s => s.Text));
        var bAdded   = string.Concat(b.Where(s => s.Kind == CharDiff.SegmentKind.Added)  .Select(s => s.Text));
        // "50" and "100" share the digit "0" → refinement should pull
        // it into the Equal segment and only highlight 5 vs 10.
        Assert.DoesNotContain("VARCHAR", aRemoved);
        Assert.DoesNotContain("VARCHAR", bAdded);
        Assert.True(aRemoved.Length <= 2, $"Removed too wide: '{aRemoved}'");
        Assert.True(bAdded.Length <= 3, $"Added too wide: '{bAdded}'");
    }

    [Fact]
    public void Tabs_vs_spaces_highlights_only_the_tab_or_space_chars()
    {
        var (a, b) = CharDiff.Diff("\tSELECT 1", "    SELECT 1");
        // "SELECT 1" intact on both sides (text after the indent).
        Assert.Contains(a, s => s.Kind == CharDiff.SegmentKind.Equal && s.Text.Contains("SELECT"));
        Assert.Contains(b, s => s.Kind == CharDiff.SegmentKind.Equal && s.Text.Contains("SELECT"));
        // The tab on A's side is Removed; the four spaces on B's side are Added.
        var aRemoved = string.Concat(a.Where(s => s.Kind == CharDiff.SegmentKind.Removed).Select(s => s.Text));
        var bAdded   = string.Concat(b.Where(s => s.Kind == CharDiff.SegmentKind.Added)  .Select(s => s.Text));
        Assert.Equal("\t",     aRemoved);
        Assert.Equal("    ",   bAdded);
    }

    [Fact]
    public void Trailing_newline_difference_only_affects_the_last_line()
    {
        // CRLF / LF / no-trailing-newline normalisation handled in
        // LineAligner.Split — same line content should produce ALL-Same.
        var (a, b) = LineAligner.AlignPair("foo\r\nbar", "foo\nbar");
        Assert.All(a, l => Assert.Equal(LineState.Same, l.State));
        Assert.All(b, l => Assert.Equal(LineState.Same, l.State));
    }

    [Fact]
    public void Pure_punctuation_change_highlights_only_the_punctuation()
    {
        // Comma vs semicolon at end of statement.
        var (a, b) = CharDiff.Diff("SELECT 1,", "SELECT 1;");
        var aRemoved = string.Concat(a.Where(s => s.Kind == CharDiff.SegmentKind.Removed).Select(s => s.Text));
        var bAdded   = string.Concat(b.Where(s => s.Kind == CharDiff.SegmentKind.Added)  .Select(s => s.Text));
        Assert.Equal(",", aRemoved);
        Assert.Equal(";", bAdded);
    }

    [Fact]
    public void Word_with_typo_in_middle_pulls_common_prefix_and_suffix_into_equal()
    {
        // "Customers" → "Cstomers" (lost a 'u'). Both share "Cstomers"
        // as a common subsequence, so char-level refinement should keep
        // the prefix + suffix Equal and isolate the "u" as Removed.
        var (a, b) = CharDiff.Diff("Customers", "Cstomers");
        var aRemoved = string.Concat(a.Where(s => s.Kind == CharDiff.SegmentKind.Removed).Select(s => s.Text));
        Assert.Equal("u", aRemoved);
    }

    [Fact]
    public void Coalesce_invariant_does_not_throw_on_match_match_delete_match_pattern()
    {
        // The pattern that triggered the ArgumentOutOfRangeException
        // before the LcsSegments-Coalesce fix: two matches, a delete,
        // then a match. After coalescing, A would have 3 Equal-bearing
        // segments and B only 1 (all merged), and the walker would read
        // past bTokens.Count. Assert it just returns now.
        for (int i = 0; i < 50; i++)
        {
            // Vary the inputs so we hit many different LCS shapes.
            var a = $"prefix_{i} something_{i} BAR_{i} suffix_{i}";
            var b = $"prefix_{i} something_{i} suffix_{i}";
            var (aSeg, bSeg) = CharDiff.Diff(a, b);
            Assert.NotEmpty(aSeg);
            Assert.NotEmpty(bSeg);
        }
    }

    [Fact]
    public void AlignPair_declare_block_with_whitespace_only_diffs_populates_segments()
    {
        // Reproduces the user's reported failure: a DECLARE block where
        // every line differs only in column-padding whitespace. The
        // screenshot showed all 336 lines painted whole-line amber,
        // meaning Segments came back null. This test asserts each
        // Different line carries a non-null, non-empty Segments list so
        // the renderer can paint only the changed bits, not the whole line.
        var a = string.Join("\n", new[]
        {
            "DECLARE",
            "    @Result        INT    = 0,",
            "    @INVItemNo     INT,",
            "    @ReferenceID   INT,",
            "    @ReferenceNo   NVARCHAR(25),",
        });
        // Same lines, but every column is padded with a couple extra spaces.
        var b = string.Join("\n", new[]
        {
            "DECLARE",
            "        @Result          INT      = 0,",
            "        @INVItemNo       INT,",
            "        @ReferenceID     INT,",
            "        @ReferenceNo     NVARCHAR(25),",
        });

        var (aLines, bLines) = LineAligner.AlignPair(a, b);
        Assert.Equal(5, aLines.Count);
        Assert.Equal(5, bLines.Count);

        // "DECLARE" matches on both sides.
        Assert.Equal(LineState.Same, aLines[0].State);

        // Every other line should be Different AND carry segments — that's
        // the only way the renderer can highlight only the whitespace.
        for (int i = 1; i < 5; i++)
        {
            Assert.Equal(LineState.Different, aLines[i].State);
            Assert.Equal(LineState.Different, bLines[i].State);
            Assert.NotNull(aLines[i].Segments);
            Assert.NotNull(bLines[i].Segments);
            Assert.True(aLines[i].Segments!.Count > 0,
                $"aLines[{i}].Segments was empty — renderer would fall back to whole-line amber");
            Assert.True(bLines[i].Segments!.Count > 0,
                $"bLines[{i}].Segments was empty — renderer would fall back to whole-line amber");

            // The non-whitespace tokens should appear as Equal segments;
            // only the whitespace runs should be Removed/Added.
            var aRemovedText = string.Concat(
                aLines[i].Segments!.Where(s => s.Kind == CharDiff.SegmentKind.Removed).Select(s => s.Text));
            var bAddedText = string.Concat(
                bLines[i].Segments!.Where(s => s.Kind == CharDiff.SegmentKind.Added).Select(s => s.Text));
            // All removed text on A and added text on B should be whitespace
            // (this is exactly the user's complaint — they want non-whitespace
            // chars left plain when only whitespace changed).
            Assert.True(string.IsNullOrWhiteSpace(aRemovedText),
                $"aLines[{i}] Removed text was '{aRemovedText}' — expected whitespace-only");
            Assert.True(string.IsNullOrWhiteSpace(bAddedText),
                $"bLines[{i}] Added text was '{bAddedText}' — expected whitespace-only");
        }
    }
}
