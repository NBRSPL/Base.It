using System.Text;

namespace Base.It.Core.Diff;

/// <summary>
/// Intra-line diff: given two strings, returns the segments for each
/// side describing what's identical / removed / added.
///
/// <para><b>Two-pass algorithm</b> (modeled on diffchecker.com's
/// word-with-char-refinement behaviour, which the user explicitly
/// referenced as the bar to clear):</para>
///
/// <list type="number">
///   <item><b>Token-level LCS</b> — words (alphanumeric+'_' runs) stay
///         intact so a "Users → Customers" swap reads as one
///         replacement, not a stew of single-char matches that happen
///         to line up. Punctuation and whitespace tokenise per
///         <em>character</em>: a 4-space indent vs a 2-space indent
///         matches the first 2 spaces and only marks the extra 2 as
///         removed, instead of repainting the whole indent.</item>
///   <item><b>Char-level refinement</b> — for every region where the
///         token diff produced both Removed text (A side) and Added
///         text (B side), re-run LCS on the raw characters of that
///         region and substitute the result. "VARCHAR(50)" →
///         "VARCHAR(100)" highlights only "5" → "10" inside the
///         parens, not the whole literal.</item>
/// </list>
///
/// Equal regions emerge plain on both sides. Pure inserts / deletes
/// (anchored to only one side) stay as one big Added / Removed
/// segment — that's the same shape diffchecker uses for added or
/// removed words.
/// </summary>
public static class CharDiff
{
    public enum SegmentKind { Equal, Removed, Added }

    /// <summary>One contiguous run of text with a single kind.</summary>
    public sealed record DiffSegment(string Text, SegmentKind Kind);

    /// <summary>
    /// Diff two strings and return the segment-sequences for each side.
    /// <list type="bullet">
    ///   <item>A-segments use <c>Equal</c> + <c>Removed</c> (nothing
    ///         is added relative to A's perspective).</item>
    ///   <item>B-segments use <c>Equal</c> + <c>Added</c>.</item>
    /// </list>
    /// </summary>
    public static (IReadOnlyList<DiffSegment> A, IReadOnlyList<DiffSegment> B) Diff(string a, string b)
    {
        // Common short-circuits keep the worst-case O(m·n) table off
        // the hot path. Equal strings are the most common case for the
        // batch sync-check pass and need to be fast.
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b))
            return (Array.Empty<DiffSegment>(), Array.Empty<DiffSegment>());
        if (a == b)
            return (new[] { new DiffSegment(a, SegmentKind.Equal) },
                    new[] { new DiffSegment(b, SegmentKind.Equal) });
        if (string.IsNullOrEmpty(a))
            return (Array.Empty<DiffSegment>(),
                    new[] { new DiffSegment(b, SegmentKind.Added) });
        if (string.IsNullOrEmpty(b))
            return (new[] { new DiffSegment(a, SegmentKind.Removed) },
                    Array.Empty<DiffSegment>());

        // Pass 1: token-level diff. Words stay whole; whitespace and
        // punctuation tokenise per-char so changes at that granularity
        // highlight exactly what differs.
        var (aTokens, bTokens) = LcsSegments(Tokenize(a), Tokenize(b));

        // Pass 2: walk both segment lists in parallel; for every
        // region where both sides have content between the same
        // Equal anchors, re-diff at the raw-character level and
        // splice the refined segments in.
        var aRefined = new List<DiffSegment>();
        var bRefined = new List<DiffSegment>();
        int ai = 0, bi = 0;
        while (ai < aTokens.Count || bi < bTokens.Count)
        {
            // Accumulate all the non-equal text on each side until the
            // next Equal anchor. We do this in parallel because the
            // Equal anchors line up by construction — LCS picked them.
            var aBuf = new StringBuilder();
            while (ai < aTokens.Count && aTokens[ai].Kind != SegmentKind.Equal)
            {
                aBuf.Append(aTokens[ai].Text);
                ai++;
            }
            var bBuf = new StringBuilder();
            while (bi < bTokens.Count && bTokens[bi].Kind != SegmentKind.Equal)
            {
                bBuf.Append(bTokens[bi].Text);
                bi++;
            }

            // Both sides have content → mutual replacement, refine at
            // char granularity so "Customer" → "Customers" highlights
            // just the trailing "s" instead of repainting the whole
            // word red+green.
            if (aBuf.Length > 0 && bBuf.Length > 0)
            {
                var (aChars, bChars) = LcsSegments(SplitChars(aBuf.ToString()), SplitChars(bBuf.ToString()));
                foreach (var seg in aChars) aRefined.Add(seg);
                foreach (var seg in bChars) bRefined.Add(seg);
            }
            else if (aBuf.Length > 0)
            {
                aRefined.Add(new DiffSegment(aBuf.ToString(), SegmentKind.Removed));
            }
            else if (bBuf.Length > 0)
            {
                bRefined.Add(new DiffSegment(bBuf.ToString(), SegmentKind.Added));
            }

            // Consume the matching Equal anchors on both sides. Either
            // we ran out of tokens (loop exits) or both lists are now
            // pointing at an Equal segment with identical text.
            if (ai < aTokens.Count && aTokens[ai].Kind == SegmentKind.Equal)
            {
                aRefined.Add(aTokens[ai]);
                bRefined.Add(bTokens[bi]);
                ai++; bi++;
            }
        }

        return (Coalesce(aRefined), Coalesce(bRefined));
    }

    /// <summary>
    /// Token-level LCS that returns segment lists for both sides.
    /// Generic over <see cref="string"/> "tokens": call it with words +
    /// per-char whitespace + per-char punctuation for the first pass,
    /// and call it again with single-char "tokens" for the char-level
    /// refinement. Identical logic either way.
    ///
    /// <para>The returned lists are <b>uncoalesced</b> — each LCS match
    /// emits exactly ONE Equal segment on both sides, and each
    /// delete/insert emits exactly ONE Removed/Added segment on its
    /// side. This 1:1 correspondence of Equal anchors is what the
    /// second-pass walker in <see cref="Diff"/> relies on: when ai
    /// reaches an Equal in aTokens, bi is guaranteed to be at the
    /// matching Equal in bTokens. Coalescing here would collapse
    /// consecutive Equal anchors asymmetrically (A and B coalesce
    /// independently based on which side has inserts/deletes between
    /// matches), the anchor counts would diverge, and the walker
    /// would read past the end of bTokens — the
    /// ArgumentOutOfRangeException the user was hitting.</para>
    /// </summary>
    private static (IReadOnlyList<DiffSegment> A, IReadOnlyList<DiffSegment> B) LcsSegments(
        IReadOnlyList<string> aTokens, IReadOnlyList<string> bTokens)
    {
        int m = aTokens.Count, n = bTokens.Count;
        if (m == 0 && n == 0) return (Array.Empty<DiffSegment>(), Array.Empty<DiffSegment>());
        if (m == 0) return (Array.Empty<DiffSegment>(),
                            new[] { new DiffSegment(string.Concat(bTokens), SegmentKind.Added) });
        if (n == 0) return (new[] { new DiffSegment(string.Concat(aTokens), SegmentKind.Removed) },
                            Array.Empty<DiffSegment>());

        var dp = new int[m + 1, n + 1];
        for (int i = 1; i <= m; i++)
        for (int j = 1; j <= n; j++)
            dp[i, j] = aTokens[i - 1] == bTokens[j - 1]
                ? dp[i - 1, j - 1] + 1
                : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        // Backtrack from (m, n) → (0, 0) producing a reversed list of
        // operations; reverse at the end so callers see top-down order.
        // One token per emit — NO merging at this layer.
        var aBack = new List<DiffSegment>(m);
        var bBack = new List<DiffSegment>(n);
        int x = m, y = n;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && aTokens[x - 1] == bTokens[y - 1])
            {
                aBack.Add(new DiffSegment(aTokens[x - 1], SegmentKind.Equal));
                bBack.Add(new DiffSegment(bTokens[y - 1], SegmentKind.Equal));
                x--; y--;
            }
            else if (y > 0 && (x == 0 || dp[x, y - 1] >= dp[x - 1, y]))
            {
                bBack.Add(new DiffSegment(bTokens[y - 1], SegmentKind.Added));
                y--;
            }
            else
            {
                aBack.Add(new DiffSegment(aTokens[x - 1], SegmentKind.Removed));
                x--;
            }
        }
        aBack.Reverse();
        bBack.Reverse();
        return (aBack, bBack);
    }

    /// <summary>
    /// Tokenise for the first-pass diff: word runs are kept intact
    /// (so identifier swaps highlight whole-word, not letter-by-letter),
    /// but whitespace and punctuation emit ONE TOKEN PER CHARACTER so
    /// indent changes / extra spaces / new commas line up at the
    /// character level. The user's "diffchecker.com behaviour" is
    /// exactly that distinction.
    /// </summary>
    internal static List<string> Tokenize(string s)
    {
        var tokens = new List<string>(s.Length / 2 + 1);
        int i = 0;
        while (i < s.Length)
        {
            var ch = s[i];
            if (IsWordChar(ch))
            {
                int start = i;
                while (i < s.Length && IsWordChar(s[i])) i++;
                tokens.Add(s.Substring(start, i - start));
            }
            else
            {
                // Single non-word char per token. This includes every
                // whitespace char individually — LCS over per-char
                // whitespace gives precise highlights for indent /
                // spacing differences.
                tokens.Add(s.Substring(i, 1));
                i++;
            }
        }
        return tokens;
    }

    /// <summary>
    /// Tokeniser for the char-level refinement pass — emits each
    /// character as its own token. Used only on the "mismatch text"
    /// between Equal anchors, so the cost is bounded by the size of
    /// modified regions, not the whole line.
    /// </summary>
    private static List<string> SplitChars(string s)
    {
        var list = new List<string>(s.Length);
        for (int i = 0; i < s.Length; i++) list.Add(s.Substring(i, 1));
        return list;
    }

    private static bool IsWordChar(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
        (c >= '0' && c <= '9') || c == '_';

    /// <summary>
    /// Walk an already-ordered list of segments and merge consecutive
    /// same-kind segments into one. Called only at the very end of
    /// <see cref="Diff"/> — the second-pass walker needs the
    /// uncoalesced form to align Equal anchors 1:1 across both sides,
    /// so we coalesce ONCE on the final result purely as a render
    /// optimisation (fewer Run objects per line).
    /// </summary>
    private static IReadOnlyList<DiffSegment> Coalesce(List<DiffSegment> segments)
    {
        if (segments.Count == 0) return segments;
        var result = new List<DiffSegment>(segments.Count);
        var sb = new StringBuilder(segments[0].Text);
        var current = segments[0].Kind;
        for (int i = 1; i < segments.Count; i++)
        {
            var s = segments[i];
            if (s.Kind == current) sb.Append(s.Text);
            else
            {
                result.Add(new DiffSegment(sb.ToString(), current));
                sb.Clear();
                sb.Append(s.Text);
                current = s.Kind;
            }
        }
        if (sb.Length > 0) result.Add(new DiffSegment(sb.ToString(), current));
        return result;
    }
}
