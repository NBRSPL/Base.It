namespace Base.It.Core.Diff;

public enum LineState { Same, Different }

/// <summary>
/// One line as it appears in one pane of the diff view.
///
/// <para><see cref="Segments"/> is the intra-line, char-aware diff
/// produced by <see cref="LineAligner.AlignPair"/>: when present the
/// renderer paints only the changed substrings, not the whole line.
/// Null when the line is unchanged, or when the aligner ran in N-way
/// mode (no single pair to compute char-diff against).</para>
///
/// <para><see cref="PairIndex"/> is the line index of the matched
/// counterpart on the other pane — used by change navigation to jump
/// both panes to the same change. -1 for inserts / deletes that have
/// no counterpart.</para>
/// </summary>
public sealed record AlignedPaneLine(
    int    Number,
    string Text,
    LineState State,
    IReadOnlyList<CharDiff.DiffSegment>? Segments = null,
    int    PairIndex = -1);

/// <summary>
/// Line-level diff with optional char-level refinement for 2-pane
/// previews. Two entry points:
///
/// <list type="bullet">
///   <item><see cref="Align"/> — N-way (used by multi-target preview).
///         Reports a line as <c>Same</c> only when it matches a peer
///         in every other input. Char-level segments are NOT
///         computed in this mode; one source line may pair with
///         different lines in different peers, so a single segment
///         list would be misleading.</item>
///   <item><see cref="AlignPair"/> — 2-way (Sync screen / Batch
///         preview with one target / snapshot diff). Runs line-LCS
///         to find paired lines, then <see cref="CharDiff.Diff"/>
///         on each non-equal pair so the renderer can highlight just
///         the changed substring. Pure inserts / deletes get
///         <c>State = Different</c> with no segments.</item>
/// </list>
/// </summary>
public static class LineAligner
{
    public static IReadOnlyList<AlignedPaneLine> Align(string self, IEnumerable<string> others)
    {
        var selfLines = Split(self);
        var result = new AlignedPaneLine[selfLines.Length];

        // Start optimistic: every line is Same until proven otherwise.
        var same = new bool[selfLines.Length];
        for (int i = 0; i < same.Length; i++) same[i] = true;

        int peerCount = 0;
        foreach (var peer in others)
        {
            peerCount++;
            var peerLines = Split(peer);
            if (peerLines.Length == 0)
            {
                // No peer content -> no matches possible.
                for (int i = 0; i < same.Length; i++) same[i] = false;
                continue;
            }
            var matched = LcsMatches(selfLines, peerLines);
            for (int i = 0; i < same.Length; i++) same[i] = same[i] && matched[i];
        }

        if (peerCount == 0)
            for (int i = 0; i < same.Length; i++) same[i] = true;

        for (int i = 0; i < selfLines.Length; i++)
            result[i] = new AlignedPaneLine(
                i + 1, selfLines[i],
                same[i] ? LineState.Same : LineState.Different);
        return result;
    }

    /// <summary>
    /// 2-pane aligner: line-LCS plus char-level refinement on paired
    /// differing lines. Returns lines for both sides; line N on the A
    /// side carries <see cref="AlignedPaneLine.PairIndex"/> pointing
    /// to its B-side counterpart (or -1 for inserts / deletes).
    ///
    /// <para>The pair index lets the change-navigation buttons jump
    /// both scroll viewers to the same change without re-computing the
    /// alignment.</para>
    /// </summary>
    public static (IReadOnlyList<AlignedPaneLine> A, IReadOnlyList<AlignedPaneLine> B) AlignPair(string a, string b)
    {
        var aLines = Split(a);
        var bLines = Split(b);

        // Edge case: one side empty → other side is "all added" (or
        // "all removed" from the empty side's perspective). No char
        // diff needed; nothing to align against.
        if (aLines.Length == 0)
        {
            var bResult = new AlignedPaneLine[bLines.Length];
            for (int i = 0; i < bLines.Length; i++)
                bResult[i] = new AlignedPaneLine(i + 1, bLines[i], LineState.Different);
            return (Array.Empty<AlignedPaneLine>(), bResult);
        }
        if (bLines.Length == 0)
        {
            var aResult = new AlignedPaneLine[aLines.Length];
            for (int i = 0; i < aLines.Length; i++)
                aResult[i] = new AlignedPaneLine(i + 1, aLines[i], LineState.Different);
            return (aResult, Array.Empty<AlignedPaneLine>());
        }

        // Walk a 2-way LCS backtrace producing line correspondences:
        //   match(i, j) → A[i] == B[j], both Same
        //   delete(i)   → A[i] has no B-counterpart, Different no-pair
        //   insert(j)   → B[j] has no A-counterpart, Different no-pair
        //   replace(i, j) → A[i] and B[j] paired but text differs,
        //                   char-diff'd; both Different with segments
        var pairs = WalkLcsPairs(aLines, bLines);

        var aResult2 = new AlignedPaneLine[aLines.Length];
        var bResult2 = new AlignedPaneLine[bLines.Length];

        foreach (var p in pairs)
        {
            if (p.IsMatch)
            {
                aResult2[p.AIndex] = new AlignedPaneLine(p.AIndex + 1, aLines[p.AIndex], LineState.Same, PairIndex: p.BIndex);
                bResult2[p.BIndex] = new AlignedPaneLine(p.BIndex + 1, bLines[p.BIndex], LineState.Same, PairIndex: p.AIndex);
            }
            else if (p.IsReplace)
            {
                var (aSegs, bSegs) = CharDiff.Diff(aLines[p.AIndex], bLines[p.BIndex]);
                aResult2[p.AIndex] = new AlignedPaneLine(p.AIndex + 1, aLines[p.AIndex], LineState.Different, aSegs, p.BIndex);
                bResult2[p.BIndex] = new AlignedPaneLine(p.BIndex + 1, bLines[p.BIndex], LineState.Different, bSegs, p.AIndex);
            }
            else if (p.AIndex >= 0)
            {
                // Pure delete on A side — no B counterpart to char-diff
                // against, so the whole line is one Removed segment.
                // Renderer then paints it red rather than falling back
                // to the legacy whole-line amber.
                aResult2[p.AIndex] = new AlignedPaneLine(
                    p.AIndex + 1, aLines[p.AIndex], LineState.Different,
                    WholeLineSegment(aLines[p.AIndex], CharDiff.SegmentKind.Removed));
            }
            else if (p.BIndex >= 0)
            {
                bResult2[p.BIndex] = new AlignedPaneLine(
                    p.BIndex + 1, bLines[p.BIndex], LineState.Different,
                    WholeLineSegment(bLines[p.BIndex], CharDiff.SegmentKind.Added));
            }
        }

        // Backstop: any A index the walker missed becomes a whole-line
        // Removed (and any B index missed becomes whole-line Added).
        // Without this, a zipper bug or unhandled edge case would
        // leave Segments null and the renderer would paint amber —
        // which is precisely the "spaces highlight whole line" symptom
        // we're trying to eliminate.
        for (int i = 0; i < aResult2.Length; i++)
            aResult2[i] ??= new AlignedPaneLine(
                i + 1, aLines[i], LineState.Different,
                WholeLineSegment(aLines[i], CharDiff.SegmentKind.Removed));
        for (int j = 0; j < bResult2.Length; j++)
            bResult2[j] ??= new AlignedPaneLine(
                j + 1, bLines[j], LineState.Different,
                WholeLineSegment(bLines[j], CharDiff.SegmentKind.Added));

        return (aResult2, bResult2);
    }

    /// <summary>
    /// Build a single-segment list wrapping a whole line as one kind —
    /// used for pure inserts / deletes that have no counterpart to
    /// char-diff against. Empty lines return null so the renderer
    /// doesn't waste an empty Run.
    /// </summary>
    private static IReadOnlyList<CharDiff.DiffSegment>? WholeLineSegment(string line, CharDiff.SegmentKind kind)
        => string.IsNullOrEmpty(line)
            ? null
            : new[] { new CharDiff.DiffSegment(line, kind) };

    /// <summary>
    /// Walk the LCS backtrace producing line-correspondence pairs.
    /// Consecutive unmatched lines on opposite sides are zipped into
    /// "replace" pairs (delete + insert at the same alignment point)
    /// so the char-diff can highlight just the substring difference.
    /// Leftover unmatched lines on one side become pure inserts /
    /// deletes with no pair.
    /// </summary>
    private static List<LinePair> WalkLcsPairs(string[] a, string[] b)
    {
        int m = a.Length, n = b.Length;
        var dp = new int[m + 1, n + 1];
        for (int i = 1; i <= m; i++)
        for (int j = 1; j <= n; j++)
            dp[i, j] = a[i - 1] == b[j - 1]
                ? dp[i - 1, j - 1] + 1
                : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        // Walk from (m, n) → (0, 0) producing a reversed list of
        // operations; reverse it at the end so callers see top-down.
        var ops = new List<LinePair>();
        int x = m, y = n;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && a[x - 1] == b[y - 1])
            {
                ops.Add(new LinePair(x - 1, y - 1, IsMatch: true,   IsReplace: false));
                x--; y--;
            }
            else if (y > 0 && (x == 0 || dp[x, y - 1] >= dp[x - 1, y]))
            {
                ops.Add(new LinePair(-1, y - 1, IsMatch: false, IsReplace: false));
                y--;
            }
            else
            {
                ops.Add(new LinePair(x - 1, -1, IsMatch: false, IsReplace: false));
                x--;
            }
        }
        ops.Reverse();

        // Second pass: every run of consecutive non-match ops is a
        // "modified block" — A-side lines deleted, B-side lines added.
        // Pair them up index-by-index so as many lines as possible
        // become Replaces (and get char-level diff highlighting).
        // Excess on one side stays as pure delete or insert (which
        // renders as a whole-line highlight — correct for actual
        // inserts / deletes).
        //
        // The earlier "zip the first adjacent pair only" logic
        // dropped most pairings whenever more than one line changed
        // in a row, which is exactly when the user notices the
        // whole-line highlight bug. This rewrite catches blocks of
        // any size.
        var zipped = new List<LinePair>(ops.Count);
        int k = 0;
        while (k < ops.Count)
        {
            if (ops[k].IsMatch) { zipped.Add(ops[k]); k++; continue; }

            int runStart = k;
            while (k < ops.Count && !ops[k].IsMatch) k++;
            int runEnd = k; // exclusive

            var deletes = new List<int>();
            var inserts = new List<int>();
            for (int p = runStart; p < runEnd; p++)
            {
                if (ops[p].AIndex >= 0) deletes.Add(ops[p].AIndex);
                if (ops[p].BIndex >= 0) inserts.Add(ops[p].BIndex);
            }

            int pairCount = Math.Min(deletes.Count, inserts.Count);
            for (int q = 0; q < pairCount; q++)
                zipped.Add(new LinePair(deletes[q], inserts[q], IsMatch: false, IsReplace: true));
            for (int q = pairCount; q < deletes.Count; q++)
                zipped.Add(new LinePair(deletes[q], -1, IsMatch: false, IsReplace: false));
            for (int q = pairCount; q < inserts.Count; q++)
                zipped.Add(new LinePair(-1, inserts[q], IsMatch: false, IsReplace: false));
        }
        return zipped;
    }

    /// <summary>
    /// One step of the line-LCS backtrace.
    /// <list type="bullet">
    ///   <item><c>IsMatch</c> — A[AIndex] equals B[BIndex] (both Same).</item>
    ///   <item><c>IsReplace</c> — A[AIndex] and B[BIndex] paired but
    ///         differ → char-diff applies.</item>
    ///   <item>otherwise — pure insert (AIndex == -1) or delete (BIndex == -1).</item>
    /// </list>
    /// </summary>
    private readonly record struct LinePair(int AIndex, int BIndex, bool IsMatch, bool IsReplace);

    /// <summary>
    /// For each index in <paramref name="a"/>, returns true if that line is
    /// part of the longest common subsequence with <paramref name="b"/>.
    /// </summary>
    private static bool[] LcsMatches(string[] a, string[] b)
    {
        int m = a.Length, n = b.Length;
        var matched = new bool[m];
        if (m == 0 || n == 0) return matched;

        var dp = new int[m + 1, n + 1];
        for (int i = 1; i <= m; i++)
        for (int j = 1; j <= n; j++)
            dp[i, j] = a[i - 1] == b[j - 1]
                ? dp[i - 1, j - 1] + 1
                : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        int I = m, J = n;
        while (I > 0 && J > 0)
        {
            if (a[I - 1] == b[J - 1]) { matched[I - 1] = true; I--; J--; }
            else if (dp[I - 1, J] >= dp[I, J - 1]) { I--; }
            else { J--; }
        }
        return matched;
    }

    private static string[] Split(string? s) =>
        string.IsNullOrEmpty(s) ? Array.Empty<string>() :
        s.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
}
