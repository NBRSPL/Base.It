namespace Base.It.Core.Diff;

public enum DiffKind { Same, Changed, MissingInA, MissingInB }

public sealed record DiffLine(int Index, string TextA, string TextB, DiffKind Kind);

/// <summary>
/// Straight line-by-line diff, good enough for the side-by-side compare view.
/// Produces one DiffLine per max(len(a), len(b)) lines.
/// </summary>
public static class LineDiffer
{
    public static IReadOnlyList<DiffLine> Compare(string? a, string? b)
    {
        var la = Split(a);
        var lb = Split(b);
        var n = Math.Max(la.Length, lb.Length);
        var result = new List<DiffLine>(n);
        for (int i = 0; i < n; i++)
        {
            var ta = i < la.Length ? la[i] : null;
            var tb = i < lb.Length ? lb[i] : null;
            DiffKind k =
                ta is null && tb is not null ? DiffKind.MissingInA :
                tb is null && ta is not null ? DiffKind.MissingInB :
                ta == tb                     ? DiffKind.Same       :
                                               DiffKind.Changed;
            result.Add(new DiffLine(i, ta ?? "", tb ?? "", k));
        }
        return result;
    }

    private static string[] Split(string? s) =>
        string.IsNullOrEmpty(s) ? Array.Empty<string>() :
        s.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
}
