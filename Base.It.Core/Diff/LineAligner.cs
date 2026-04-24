namespace Base.It.Core.Diff;

public enum LineState { Same, Different }

public sealed record AlignedPaneLine(int Number, string Text, LineState State);

/// <summary>
/// Multi-way line-level diff using the longest-common-subsequence algorithm.
///
/// A line in <c>self</c> is reported as <c>Same</c> only when it has a matching
/// line (by LCS alignment, not by position) in <b>every</b> other input. Any
/// mismatch against any peer marks it <c>Different</c>. This gives stable
/// results when one environment has extra or reordered lines — a leading
/// blank line does NOT cascade into every following line being flagged.
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
