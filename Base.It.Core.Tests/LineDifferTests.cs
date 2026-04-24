using Base.It.Core.Diff;
using Xunit;

namespace Base.It.Core.Tests;

public class LineDifferTests
{
    [Fact]
    public void Identical_inputs_are_all_same()
    {
        var d = LineDiffer.Compare("a\nb\nc", "a\nb\nc");
        Assert.All(d, x => Assert.Equal(DiffKind.Same, x.Kind));
    }

    [Fact]
    public void Flags_changed_lines_and_extras()
    {
        var d = LineDiffer.Compare("a\nb",  "a\nB\nc");
        Assert.Equal(DiffKind.Same,       d[0].Kind);
        Assert.Equal(DiffKind.Changed,    d[1].Kind);
        Assert.Equal(DiffKind.MissingInA, d[2].Kind);
    }

    [Fact]
    public void Lineending_normalisation_does_not_affect_counts()
    {
        var d = LineDiffer.Compare("a\r\nb", "a\nb");
        Assert.Equal(2, d.Count);
        Assert.All(d, x => Assert.Equal(DiffKind.Same, x.Kind));
    }
}
