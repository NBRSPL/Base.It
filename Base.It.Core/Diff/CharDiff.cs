using System.Text;

namespace Base.It.Core.Diff;

/// <summary>
/// Intra-line diff: given two strings, returns the sequence of segments
/// for each side describing what's identical / removed / added.
///
/// <para>Built on character-level LCS. For SQL lines this is good
/// enough — typical lines are short (50–200 chars), so the O(n·m)
/// table cost is trivial and the segments produced track the actual
/// differences well (whitespace changes stay confined to the
/// whitespace, identifier changes to the identifier, etc.).</para>
///
/// <para>Tokenisation pass (whitespace + word boundaries) runs first
/// so a "FROM Users" → "FROM Customers" diff highlights the whole
/// word swap as one delete + one add, not 3 single-char
/// substitutions woven together with accidental "s" / "r" matches.
/// The post-coalesce step then merges adjacent same-kind segments back
/// into a clean run of text.</para>
///
/// <para>Why not word-only? Because a single-char typo in the middle
/// of a long identifier should still highlight just the typo, not
/// repaint the whole word. We tokenise into the smallest units that
/// still avoid LCS noise — alphanumeric+underscore runs, whitespace
/// runs, and individual punctuation chars — then LCS over those
/// tokens. That gives word-level granularity for words and
/// character-level granularity for symbols.</para>
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
        // Common short-circuits keep the worst-case O(m·n) table off the
        // path for the easy cases the differ sees most often.
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

        var aTokens = Tokenize(a);
        var bTokens = Tokenize(b);

        // Token-level LCS table. Tokens are short strings; we compare
        // by ordinal equality (case-sensitive — SQL keywords ARE case-
        // insensitive but identifier casing matters, and conflating
        // the two would paint correct case as a non-diff).
        int m = aTokens.Count, n = bTokens.Count;
        var dp = new int[m + 1, n + 1];
        for (int i = 1; i <= m; i++)
        for (int j = 1; j <= n; j++)
            dp[i, j] = aTokens[i - 1] == bTokens[j - 1]
                ? dp[i - 1, j - 1] + 1
                : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        // Backtrack from (m, n) → (0, 0) emitting token operations.
        // Using stacks so we can pop in left-to-right order when we
        // coalesce into segments.
        var aOps = new Stack<(string Token, SegmentKind Kind)>();
        var bOps = new Stack<(string Token, SegmentKind Kind)>();
        int x = m, y = n;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && aTokens[x - 1] == bTokens[y - 1])
            {
                aOps.Push((aTokens[x - 1], SegmentKind.Equal));
                bOps.Push((bTokens[y - 1], SegmentKind.Equal));
                x--; y--;
            }
            else if (y > 0 && (x == 0 || dp[x, y - 1] >= dp[x - 1, y]))
            {
                bOps.Push((bTokens[y - 1], SegmentKind.Added));
                y--;
            }
            else
            {
                aOps.Push((aTokens[x - 1], SegmentKind.Removed));
                x--;
            }
        }

        return (Coalesce(aOps), Coalesce(bOps));
    }

    /// <summary>
    /// Split <paramref name="s"/> into the smallest tokens that still
    /// produce clean diffs: word-like runs (letters + digits + '_'),
    /// whitespace runs, and individual non-word chars. This gives
    /// "FROM Users" → "FROM Customers" a clean (Users → Customers)
    /// swap instead of a stew of single-char hits.
    /// </summary>
    internal static List<string> Tokenize(string s)
    {
        var tokens = new List<string>(s.Length / 4 + 1);
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
            else if (char.IsWhiteSpace(ch))
            {
                int start = i;
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                tokens.Add(s.Substring(start, i - start));
            }
            else
            {
                // Punctuation / symbols: one char per token. Bracketing
                // characters in SQL ([, ], (, ), ,, ., =) deserve their
                // own granularity — collapsing them into multi-char
                // tokens would let "[Id]" and "[X]" share equal chars
                // by coincidence.
                tokens.Add(s.Substring(i, 1));
                i++;
            }
        }
        return tokens;
    }

    private static bool IsWordChar(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
        (c >= '0' && c <= '9') || c == '_';

    /// <summary>
    /// Walk the token stack (oldest first) and merge consecutive
    /// same-kind tokens into a single segment. Halves the number of
    /// runs the renderer has to paint for typical lines.
    /// </summary>
    private static IReadOnlyList<DiffSegment> Coalesce(Stack<(string Token, SegmentKind Kind)> ops)
    {
        var result = new List<DiffSegment>(ops.Count);
        var sb = new StringBuilder();
        SegmentKind? current = null;
        while (ops.Count > 0)
        {
            var (token, kind) = ops.Pop();
            if (current is null)
            {
                current = kind;
                sb.Append(token);
            }
            else if (current == kind)
            {
                sb.Append(token);
            }
            else
            {
                result.Add(new DiffSegment(sb.ToString(), current.Value));
                sb.Clear();
                sb.Append(token);
                current = kind;
            }
        }
        if (sb.Length > 0 && current.HasValue)
            result.Add(new DiffSegment(sb.ToString(), current.Value));
        return result;
    }
}
