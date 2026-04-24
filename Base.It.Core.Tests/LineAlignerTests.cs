using Base.It.Core.Diff;
using Xunit;

namespace Base.It.Core.Tests;

public class LineAlignerTests
{
    [Fact]
    public void No_peers_means_every_line_is_same()
    {
        var r = LineAligner.Align("a\nb\nc", Array.Empty<string>());
        Assert.All(r, x => Assert.Equal(LineState.Same, x.State));
    }

    [Fact]
    public void Identical_peers_every_line_is_same()
    {
        var r = LineAligner.Align("a\nb\nc", new[] { "a\nb\nc", "a\nb\nc" });
        Assert.All(r, x => Assert.Equal(LineState.Same, x.State));
    }

    [Fact]
    public void Inserted_line_in_peer_does_not_cascade_into_every_following_line()
    {
        //  self:        peer:
        //  line1        EXTRA
        //  line2        line1
        //  line3        line2
        //               line3
        // Positional diff would flag all three. LCS flags none.
        var r = LineAligner.Align("line1\nline2\nline3", new[] { "EXTRA\nline1\nline2\nline3" });
        Assert.Equal(LineState.Same, r[0].State);
        Assert.Equal(LineState.Same, r[1].State);
        Assert.Equal(LineState.Same, r[2].State);
    }

    [Fact]
    public void Only_truly_changed_lines_are_marked_different()
    {
        var self = "alpha\nbeta\ngamma";
        var peer = "alpha\nBETA_CHANGED\ngamma";
        var r = LineAligner.Align(self, new[] { peer });
        Assert.Equal(LineState.Same,       r[0].State);
        Assert.Equal(LineState.Different,  r[1].State);
        Assert.Equal(LineState.Same,       r[2].State);
    }

    [Fact]
    public void Line_is_same_only_when_matched_in_ALL_peers()
    {
        var self  = "alpha\nbeta\ngamma";
        var peerA = "alpha\nbeta\ngamma";
        var peerB = "alpha\nBETA\ngamma";    // beta differs here
        var r = LineAligner.Align(self, new[] { peerA, peerB });
        Assert.Equal(LineState.Same,      r[0].State);
        Assert.Equal(LineState.Different, r[1].State);   // any mismatch wins
        Assert.Equal(LineState.Same,      r[2].State);
    }

    [Fact]
    public void Empty_peer_string_marks_everything_different()
    {
        var r = LineAligner.Align("a\nb", new[] { "" });
        Assert.All(r, x => Assert.Equal(LineState.Different, x.State));
    }

    [Fact]
    public void Handles_CRLF_and_LF_identically()
    {
        var r = LineAligner.Align("a\nb\nc", new[] { "a\r\nb\r\nc\r\n" });
        Assert.All(r, x => Assert.Equal(LineState.Same, x.State));
    }
}
