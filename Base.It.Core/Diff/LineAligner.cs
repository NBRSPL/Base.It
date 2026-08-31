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
///         preview with one target / snapshot diff). This is the
///         "compare two objects" path and uses a <b>patience/histogram
///         style</b> alignment: it anchors on lines that are unique on
///         both sides (so repeated lines like <c>BEGIN</c> / <c>END</c> /
///         <c>GO</c> / <c>)</c> / blanks can't drag the alignment
///         sideways the way a plain LCS does), then within each changed
///         block pairs removed lines to the <b>most similar</b> added
///         line so <see cref="CharDiff"/> highlights related lines
///         instead of whatever happened to line up positionally.
///         Genuinely unrelated lines stay as clean whole-line
///         inserts / deletes.</item>
/// </list>
/// </summary>
public static class LineAligner
{
    // ── Tuning knobs for the 2-way (AlignPair) block pairing ─────────────
    // A removed line is paired with an added line (shown as a char-diff
    // "replace") only when their token similarity clears this bar; below it
    // they read as separate add + delete, which is what an unrelated pair
    // actually is. 0.4 keeps genuine edits paired while rejecting noise.
    private const double MinReplaceSimilarity = 0.40;
    // Safety valves so a pathologically large object can't blow up the
    // O(m·n) matrices. Real SQL definitions never approach these.
    private const long MaxDpCells   = 4_000_000;   // line-LCS fallback matrix
    private const long MaxPairCells = 1_000_000;   // block similarity matrix
    // Bound the patience recursion so an adversarial deeply-nested object
    // can't overflow the stack; beyond this a segment is diffed with the
    // flat line-LCS instead. Normal SQL never nests more than a few levels.
    private const int  MaxPatienceDepth = 400;

    public static IReadOnlyList<AlignedPaneLine> Align(string self, IEnumerable<string> others, bool ignoreWhitespace = false)
    {
        var selfLines = Split(self);
        var selfKeys  = Keys(selfLines, ignoreWhitespace);
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
            // Match on whitespace-stripped keys when ignoreWhitespace is on,
            // so lines that differ only in spaces/tabs count as equal.
            var matched = LcsMatches(selfKeys, Keys(peerLines, ignoreWhitespace));
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
    /// 2-pane aligner: patience/histogram line anchoring plus char-level
    /// refinement on paired differing lines. Returns lines for both sides;
    /// line N on the A side carries <see cref="AlignedPaneLine.PairIndex"/>
    /// pointing to its B-side counterpart (or -1 for inserts / deletes).
    ///
    /// <para>The pair index lets the change-navigation buttons jump
    /// both scroll viewers to the same change without re-computing the
    /// alignment.</para>
    /// </summary>
    public static (IReadOnlyList<AlignedPaneLine> A, IReadOnlyList<AlignedPaneLine> B) AlignPair(string a, string b, bool ignoreWhitespace = false)
    {
        var aLines = Split(a);
        var bLines = Split(b);
        // Equality (line pairing) runs on whitespace-stripped keys when
        // ignoreWhitespace is on; the displayed text stays the originals.
        var aKeys = Keys(aLines, ignoreWhitespace);
        var bKeys = Keys(bLines, ignoreWhitespace);

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

        // Patience alignment → line correspondences:
        //   match(i, j) → A[i] == B[j], both Same
        //   delete(i)   → A[i] has no B-counterpart, Different no-pair
        //   insert(j)   → B[j] has no A-counterpart, Different no-pair
        //   replace(i, j) → A[i] and B[j] paired but text differs,
        //                   char-diff'd; both Different with segments
        var pairs = WalkPatiencePairs(aKeys, bKeys);

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
                // When ignoring whitespace, a change that is only spaces/tabs is
                // not a real difference — don't paint it as a changed segment
                // (otherwise indentation shifts inside an otherwise-content
                // change still light up). The line already paired because its
                // non-whitespace content differs.
                if (ignoreWhitespace)
                {
                    aSegs = NeutralizeWhitespaceSegments(aSegs);
                    bSegs = NeutralizeWhitespaceSegments(bSegs);
                }
                aResult2[p.AIndex] = new AlignedPaneLine(p.AIndex + 1, aLines[p.AIndex], LineState.Different, aSegs, p.BIndex);
                bResult2[p.BIndex] = new AlignedPaneLine(p.BIndex + 1, bLines[p.BIndex], LineState.Different, bSegs, p.AIndex);
            }
            else if (p.AIndex >= 0)
            {
                // Pure delete on A side — no B counterpart to char-diff
                // against, so the whole line is one Removed segment.
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

        // Backstop: any index the walker missed becomes a whole-line
        // Removed / Added. Guards against a zipper bug leaving Segments
        // null (which the renderer would paint amber — the "spaces
        // highlight the whole line" symptom).
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
    /// Re-classify any Removed / Added segment whose text is entirely spaces /
    /// tabs as Equal, so the whitespace-insensitive view doesn't highlight a
    /// pure-whitespace change. The line stays a replace (its real content still
    /// differs); only the cosmetic bits stop being painted.
    /// </summary>
    private static IReadOnlyList<CharDiff.DiffSegment> NeutralizeWhitespaceSegments(IReadOnlyList<CharDiff.DiffSegment> segs)
    {
        List<CharDiff.DiffSegment>? copy = null;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (s.Kind != CharDiff.SegmentKind.Equal && IsAllWhitespace(s.Text))
            {
                copy ??= new List<CharDiff.DiffSegment>(segs);
                copy[i] = s with { Kind = CharDiff.SegmentKind.Equal };
            }
        }
        return copy ?? segs;
    }

    private static bool IsAllWhitespace(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s) if (c != ' ' && c != '\t') return false;
        return true;
    }

    // ─────────────────────── Patience alignment ───────────────────────
    //
    // Two stages:
    //   1. Produce a flat op stream (Match / Delete / Insert) via patience
    //      recursion — anchor on unique-common lines, recurse into the gaps,
    //      fall back to line-LCS only where no unique anchor exists.
    //   2. Fold each run of Delete/Insert ops (a "changed block") into
    //      similarity-paired Replaces plus leftover pure Delete/Insert.

    private enum OpKind { Match, Delete, Insert }
    private readonly record struct Op(int A, int B, OpKind Kind);

    private static List<LinePair> WalkPatiencePairs(string[] a, string[] b)
    {
        var ops = new List<Op>(a.Length + b.Length);
        PatienceDiff(a, b, 0, a.Length, 0, b.Length, ops, depth: 0);
        return ZipBlocks(ops, a, b);
    }

    /// <summary>
    /// Emit Match/Delete/Insert ops for A[aLo,aHi) vs B[bLo,bHi) in
    /// top-down order. Trims common prefix/suffix, anchors on unique-common
    /// lines (LIS to keep them non-crossing), recurses into the gaps, and
    /// only drops to a line-LCS when a segment has no unique anchor.
    /// </summary>
    private static void PatienceDiff(string[] a, string[] b, int aLo, int aHi, int bLo, int bHi, List<Op> ops, int depth)
    {
        // Recursion backstop: past the depth cap, diff this segment flatly.
        if (depth > MaxPatienceDepth) { LcsOps(a, b, aLo, aHi, bLo, bHi, ops); return; }

        // Common prefix.
        while (aLo < aHi && bLo < bHi && a[aLo] == b[bLo])
        { ops.Add(new Op(aLo, bLo, OpKind.Match)); aLo++; bLo++; }

        // Common suffix — collected now, appended after the middle so the
        // op order stays top-down.
        int aEnd = aHi, bEnd = bHi;
        List<Op>? suffix = null;
        while (aLo < aEnd && bLo < bEnd && a[aEnd - 1] == b[bEnd - 1])
        {
            aEnd--; bEnd--;
            (suffix ??= new List<Op>()).Add(new Op(aEnd, bEnd, OpKind.Match));
        }

        // Middle: [aLo,aEnd) × [bLo,bEnd).
        if (aLo >= aEnd && bLo >= bEnd)
        {
            // nothing between prefix and suffix
        }
        else if (aLo >= aEnd)
        {
            for (int j = bLo; j < bEnd; j++) ops.Add(new Op(-1, j, OpKind.Insert));
        }
        else if (bLo >= bEnd)
        {
            for (int i = aLo; i < aEnd; i++) ops.Add(new Op(i, -1, OpKind.Delete));
        }
        else
        {
            var anchors = UniqueCommonAnchors(a, b, aLo, aEnd, bLo, bEnd);
            if (anchors.Count == 0)
            {
                LcsOps(a, b, aLo, aEnd, bLo, bEnd, ops);
            }
            else
            {
                int prevA = aLo, prevB = bLo;
                foreach (var (ai, bi) in anchors)
                {
                    PatienceDiff(a, b, prevA, ai, prevB, bi, ops, depth + 1);
                    ops.Add(new Op(ai, bi, OpKind.Match));
                    prevA = ai + 1; prevB = bi + 1;
                }
                PatienceDiff(a, b, prevA, aEnd, prevB, bEnd, ops, depth + 1);
            }
        }

        // Emit the suffix (collected end→inward, so reverse to ascending).
        if (suffix is not null)
            for (int k = suffix.Count - 1; k >= 0; k--) ops.Add(suffix[k]);
    }

    /// <summary>
    /// Lines that occur exactly once on both sides within the given range,
    /// as (A index, B index) pairs, reduced to the longest non-crossing
    /// (LIS-by-B) subset so the anchors form a consistent skeleton.
    /// </summary>
    private static List<(int A, int B)> UniqueCommonAnchors(string[] a, string[] b, int aLo, int aHi, int bLo, int bHi)
    {
        var aInfo = new Dictionary<string, (int Count, int Idx)>(aHi - aLo);
        for (int i = aLo; i < aHi; i++)
            aInfo[a[i]] = aInfo.TryGetValue(a[i], out var e) ? (e.Count + 1, e.Idx) : (1, i);

        var bInfo = new Dictionary<string, (int Count, int Idx)>(bHi - bLo);
        for (int j = bLo; j < bHi; j++)
            bInfo[b[j]] = bInfo.TryGetValue(b[j], out var e) ? (e.Count + 1, e.Idx) : (1, j);

        var cands = new List<(int A, int B)>();
        foreach (var kv in aInfo)
        {
            if (kv.Value.Count != 1) continue;
            if (bInfo.TryGetValue(kv.Key, out var be) && be.Count == 1)
                cands.Add((kv.Value.Idx, be.Idx));
        }
        cands.Sort(static (x, y) => x.A.CompareTo(y.A));
        return LongestIncreasingByB(cands);
    }

    /// <summary>
    /// Longest strictly-increasing-by-B subsequence of anchor candidates
    /// already sorted by A. Standard O(k log k) patience-sort with
    /// predecessor links for reconstruction.
    /// </summary>
    private static List<(int A, int B)> LongestIncreasingByB(List<(int A, int B)> cands)
    {
        if (cands.Count == 0) return cands;

        var pileTop = new List<int>();   // cand index sitting on each pile
        var pileTopB = new List<int>();  // its B value (ascending across piles)
        var prev = new int[cands.Count];

        for (int i = 0; i < cands.Count; i++)
        {
            int bVal = cands[i].B;
            // lower_bound: first pile whose top B >= bVal (strictly increasing).
            int lo = 0, hi = pileTopB.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (pileTopB[mid] < bVal) lo = mid + 1; else hi = mid; }
            prev[i] = lo > 0 ? pileTop[lo - 1] : -1;
            if (lo == pileTop.Count) { pileTop.Add(i); pileTopB.Add(bVal); }
            else { pileTop[lo] = i; pileTopB[lo] = bVal; }
        }

        var res = new List<(int A, int B)>(pileTop.Count);
        for (int k = pileTop[^1]; k != -1; k = prev[k]) res.Add(cands[k]);
        res.Reverse();
        return res;
    }

    /// <summary>
    /// Plain line-LCS op emitter for a range with no unique anchor. Emits
    /// Match/Delete/Insert in top-down order. Degrades to a whole-range
    /// delete+insert block if the matrix would be too large (the similarity
    /// pass then still pairs related lines).
    /// </summary>
    private static void LcsOps(string[] a, string[] b, int aLo, int aHi, int bLo, int bHi, List<Op> ops)
    {
        int m = aHi - aLo, n = bHi - bLo;
        if (m == 0) { for (int j = bLo; j < bHi; j++) ops.Add(new Op(-1, j, OpKind.Insert)); return; }
        if (n == 0) { for (int i = aLo; i < aHi; i++) ops.Add(new Op(i, -1, OpKind.Delete)); return; }
        if ((long)m * n > MaxDpCells)
        {
            for (int i = aLo; i < aHi; i++) ops.Add(new Op(i, -1, OpKind.Delete));
            for (int j = bLo; j < bHi; j++) ops.Add(new Op(-1, j, OpKind.Insert));
            return;
        }

        var dp = new int[m + 1, n + 1];
        for (int i = 1; i <= m; i++)
        for (int j = 1; j <= n; j++)
            dp[i, j] = a[aLo + i - 1] == b[bLo + j - 1]
                ? dp[i - 1, j - 1] + 1
                : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        var local = new List<Op>(m + n);
        int x = m, y = n;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && a[aLo + x - 1] == b[bLo + y - 1])
            { local.Add(new Op(aLo + x - 1, bLo + y - 1, OpKind.Match)); x--; y--; }
            else if (y > 0 && (x == 0 || dp[x, y - 1] >= dp[x - 1, y]))
            { local.Add(new Op(-1, bLo + y - 1, OpKind.Insert)); y--; }
            else
            { local.Add(new Op(aLo + x - 1, -1, OpKind.Delete)); x--; }
        }
        for (int i = local.Count - 1; i >= 0; i--) ops.Add(local[i]);
    }

    /// <summary>
    /// Fold the op stream into <see cref="LinePair"/>s: matches pass
    /// through; each maximal run of Delete/Insert ops becomes a changed
    /// block whose removed and added lines are paired by similarity.
    /// </summary>
    private static List<LinePair> ZipBlocks(List<Op> ops, string[] a, string[] b)
    {
        var result = new List<LinePair>(ops.Count);
        int k = 0;
        while (k < ops.Count)
        {
            if (ops[k].Kind == OpKind.Match)
            {
                result.Add(new LinePair(ops[k].A, ops[k].B, IsMatch: true, IsReplace: false));
                k++;
                continue;
            }

            int start = k;
            while (k < ops.Count && ops[k].Kind != OpKind.Match) k++;

            var deletes = new List<int>();
            var inserts = new List<int>();
            for (int p = start; p < k; p++)
            {
                if (ops[p].Kind == OpKind.Delete) deletes.Add(ops[p].A);
                else                              inserts.Add(ops[p].B);
            }
            PairBlock(deletes, inserts, a, b, result);
        }
        return result;
    }

    /// <summary>
    /// Pair a changed block's removed lines to its added lines.
    ///   • all-delete or all-insert → pure ops.
    ///   • exactly one of each → always a Replace (a single edited line —
    ///     the commonest case; keep the char-diff even for a big rewrite).
    ///   • otherwise → similarity DP: monotonic (non-crossing) pairing of
    ///     the most-similar lines above the threshold; the rest stay pure.
    /// </summary>
    private static void PairBlock(List<int> deletes, List<int> inserts, string[] a, string[] b, List<LinePair> outp)
    {
        if (deletes.Count == 0)
        {
            foreach (var j in inserts) outp.Add(new LinePair(-1, j, IsMatch: false, IsReplace: false));
            return;
        }
        if (inserts.Count == 0)
        {
            foreach (var i in deletes) outp.Add(new LinePair(i, -1, IsMatch: false, IsReplace: false));
            return;
        }
        if (deletes.Count == 1 && inserts.Count == 1)
        {
            outp.Add(new LinePair(deletes[0], inserts[0], IsMatch: false, IsReplace: true));
            return;
        }

        int D = deletes.Count, I = inserts.Count;
        var dPaired = new bool[D];
        var iPaired = new bool[I];

        if ((long)D * I > MaxPairCells)
        {
            // Pathologically large block — positional pairing (legacy).
            int n = Math.Min(D, I);
            for (int q = 0; q < n; q++)
            {
                outp.Add(new LinePair(deletes[q], inserts[q], IsMatch: false, IsReplace: true));
                dPaired[q] = iPaired[q] = true;
            }
        }
        else
        {
            var sim = new double[D, I];
            for (int d = 0; d < D; d++)
            for (int i = 0; i < I; i++)
                sim[d, i] = LineSimilarity(a[deletes[d]], b[inserts[i]]);

            // Monotonic max-similarity pairing (only pairs clearing the bar).
            var score  = new double[D + 1, I + 1];
            var choice = new byte[D + 1, I + 1]; // 0=pair, 1=skip delete, 2=skip insert
            for (int d = 1; d <= D; d++)
            for (int i = 1; i <= I; i++)
            {
                double up   = score[d - 1, i];
                double left = score[d, i - 1];
                double diag = sim[d - 1, i - 1] >= MinReplaceSimilarity
                    ? score[d - 1, i - 1] + sim[d - 1, i - 1]
                    : double.NegativeInfinity;

                if (!double.IsNegativeInfinity(diag) && diag >= up && diag >= left)
                { score[d, i] = diag; choice[d, i] = 0; }
                else if (up >= left)
                { score[d, i] = up;   choice[d, i] = 1; }
                else
                { score[d, i] = left; choice[d, i] = 2; }
            }

            int dd = D, ii = I;
            while (dd > 0 && ii > 0)
            {
                switch (choice[dd, ii])
                {
                    case 0:
                        outp.Add(new LinePair(deletes[dd - 1], inserts[ii - 1], IsMatch: false, IsReplace: true));
                        dPaired[dd - 1] = iPaired[ii - 1] = true;
                        dd--; ii--;
                        break;
                    case 1: dd--; break;
                    default: ii--; break;
                }
            }
        }

        // Leftovers stay pure delete / insert.
        for (int d = 0; d < D; d++)
            if (!dPaired[d]) outp.Add(new LinePair(deletes[d], -1, IsMatch: false, IsReplace: false));
        for (int i = 0; i < I; i++)
            if (!iPaired[i]) outp.Add(new LinePair(-1, inserts[i], IsMatch: false, IsReplace: false));
    }

    /// <summary>
    /// Similarity of two lines in [0,1]: multiset Dice over their
    /// non-whitespace tokens (words kept whole, punctuation per-char — the
    /// same tokenisation <see cref="CharDiff"/> uses). Whitespace is ignored
    /// so indentation doesn't inflate the score. 1.0 = identical token bags.
    /// </summary>
    private static double LineSimilarity(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;

        var counts = new Dictionary<string, int>();
        int aCount = 0;
        foreach (var t in CharDiff.Tokenize(a))
        {
            if (IsWhitespaceToken(t)) continue;
            aCount++;
            counts[t] = counts.TryGetValue(t, out var c) ? c + 1 : 1;
        }

        int inter = 0, bCount = 0;
        foreach (var t in CharDiff.Tokenize(b))
        {
            if (IsWhitespaceToken(t)) continue;
            bCount++;
            if (counts.TryGetValue(t, out var c) && c > 0) { inter++; counts[t] = c - 1; }
        }

        if (aCount == 0 && bCount == 0) return 1.0;   // both whitespace-only
        if (aCount == 0 || bCount == 0) return 0.0;
        return 2.0 * inter / (aCount + bCount);
    }

    private static bool IsWhitespaceToken(string t) => t.Length == 1 && (t[0] == ' ' || t[0] == '\t');

    /// <summary>
    /// One resolved line correspondence.
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
    /// Used by the N-way <see cref="Align"/> to mark Same/Different.
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

    /// <summary>
    /// Comparison keys for a set of lines. When <paramref name="ignoreWhitespace"/>
    /// is true every space and tab is stripped so lines that differ only in
    /// indentation / spacing compare equal; otherwise the keys are the lines
    /// themselves. Either way the caller keeps the original lines for display.
    /// </summary>
    private static string[] Keys(string[] lines, bool ignoreWhitespace)
    {
        if (!ignoreWhitespace) return lines;
        var keys = new string[lines.Length];
        for (int i = 0; i < lines.Length; i++) keys[i] = StripWhitespace(lines[i]);
        return keys;
    }

    /// <summary>
    /// The "Ignore spaces &amp; tabs" comparison key: strip EVERY space and
    /// tab. Deliberately simple (not literal- or comment-aware): the diff view
    /// is meant to hide all whitespace when the toggle is on, and a
    /// literal/comment-aware pass mis-fired on apostrophes inside comments
    /// (e.g. <c>-- the row's value</c>), leaving a line washed while its
    /// per-character diff was neutralised. The in-sync hash keeps its own
    /// (token-based, literal-preserving) rule; this key is intentionally more
    /// aggressive so the visual "ignore whitespace" truly ignores all of it.
    /// </summary>
    private static string StripWhitespace(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (c != ' ' && c != '\t') sb.Append(c);
        return sb.ToString();
    }
}
