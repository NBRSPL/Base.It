using Base.It.Core.Diff;
using Xunit;

namespace Base.It.Core.Tests;

/// <summary>
/// Behaviour of the patience/similarity 2-way aligner (<see cref="LineAligner.AlignPair"/>):
/// anchor on unique-common lines, then pair a changed block's removed and
/// added lines by content similarity rather than position.
/// </summary>
public class LineAlignerPatienceTests
{
    [Fact]
    public void Pairs_similar_lines_by_content_not_position()
    {
        // Changed block: two deletes, two inserts. Positional pairing would
        // pair delete[0] with insert[0] (unrelated). Similarity pairing must
        // pair the "shared content" lines across positions and leave the
        // unrelated lines as pure add / delete.
        var a = "totally different old line\nshared content here X";
        var b = "shared content here Y\nbrand new unrelated stuff";

        var (A, B) = LineAligner.AlignPair(a, b);

        // "shared content here X" (A[1]) pairs with "shared content here Y" (B[0]).
        Assert.Equal(0, A[1].PairIndex);
        Assert.Equal(1, B[0].PairIndex);
        Assert.Equal(LineState.Different, A[1].State);
        Assert.NotNull(A[1].Segments);   // rendered as a char-diff replace

        // The unrelated lines have no counterpart.
        Assert.Equal(-1, A[0].PairIndex);
        Assert.Equal(-1, B[1].PairIndex);
    }

    [Fact]
    public void Sql_block_pairs_the_edited_line_and_leaves_the_new_line_pure()
    {
        // A column gains a trailing comma AND a new column is inserted BEFORE
        // it. The edited line must pair with its comma'd self, not with the
        // brand-new line that happens to sit at the same offset.
        var a = "SELECT\n       c\nFROM t";
        var b = "SELECT\n       d\n       c,\nFROM t";

        var (A, B) = LineAligner.AlignPair(a, b);

        Assert.Equal(2, A[1].PairIndex);      // "       c" -> "       c,"
        Assert.Equal(1, B[2].PairIndex);
        Assert.Equal(-1, B[1].PairIndex);     // "       d" is a pure insert
        Assert.Equal(LineState.Same, A[0].State);   // SELECT unchanged
        Assert.Equal(LineState.Same, A[2].State);   // FROM t unchanged
    }

    [Fact]
    public void Unrelated_block_lines_stay_pure_add_and_delete()
    {
        var a = "aaa bbb ccc\nddd eee fff";
        var b = "ggg hhh iii\njjj kkk lll";

        var (A, B) = LineAligner.AlignPair(a, b);

        Assert.All(A, l => Assert.Equal(-1, l.PairIndex));
        Assert.All(B, l => Assert.Equal(-1, l.PairIndex));
        Assert.All(A, l => Assert.Equal(LineState.Different, l.State));
        Assert.All(B, l => Assert.Equal(LineState.Different, l.State));
    }

    [Fact]
    public void Change_inside_repeated_block_keeps_context_lines_same()
    {
        // Repeated BEGIN / END lines must not drag the alignment sideways:
        // only the single changed body line differs.
        var a = "BEGIN\n  x = 1\nEND\nBEGIN\n  y = 1\nEND";
        var b = "BEGIN\n  x = 1\nEND\nBEGIN\n  y = 2\nEND";

        var (A, _) = LineAligner.AlignPair(a, b);

        Assert.Equal(LineState.Different, A[4].State);   // "  y = 1" -> "  y = 2"
        for (int i = 0; i < A.Count; i++)
            if (i != 4) Assert.Equal(LineState.Same, A[i].State);
    }

    [Fact]
    public void Identical_inputs_all_same_with_pair_indices()
    {
        var (A, B) = LineAligner.AlignPair("a\nb\nc", "a\nb\nc");

        Assert.All(A, l => Assert.Equal(LineState.Same, l.State));
        Assert.All(B, l => Assert.Equal(LineState.Same, l.State));
        Assert.Equal(1, A[1].PairIndex);   // b <-> b
    }

    [Fact]
    public void Single_line_change_is_a_char_diff_replace()
    {
        var (A, _) = LineAligner.AlignPair("SELECT a\nFROM t1", "SELECT a\nFROM t2");

        Assert.Equal(LineState.Same, A[0].State);
        Assert.Equal(LineState.Different, A[1].State);
        Assert.Equal(1, A[1].PairIndex);
        Assert.NotNull(A[1].Segments);     // char-level highlight, not whole-line
    }

    [Fact]
    public void Pure_insert_and_delete_have_no_pair()
    {
        // Inserting a middle line: the added line is a pure insert; the
        // surrounding lines stay matched.
        var (A, B) = LineAligner.AlignPair("a\nc", "a\nb\nc");

        Assert.All(A, l => Assert.Equal(LineState.Same, l.State));
        Assert.Equal(LineState.Different, B[1].State);   // inserted "b"
        Assert.Equal(-1, B[1].PairIndex);
    }
}
