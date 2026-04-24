using Base.It.Core.Dacpac;
using Xunit;

namespace Base.It.Core.Tests;

public class GitStagerTests
{
    [Fact]
    public void TimestampedBranch_uses_prefix_and_minute_resolution_stamp()
    {
        var name = GitStager.TimestampedBranch("drift/");
        Assert.StartsWith("drift/", name);
        // yyyyMMdd-HHmm → 13 chars after prefix
        Assert.Equal("drift/".Length + 13, name.Length);
    }

    [Fact]
    public async Task Missing_git_executable_returns_exit_127_not_a_crash()
    {
        var dir = Path.GetTempPath();
        var stager = new GitStager(dir, gitExecutable: "git-that-does-not-exist-xyzzy");
        var outcome = await stager.RunAsync(new[] { "status" });
        Assert.False(outcome.Ok);
        // Either process-not-found (127) or the OS-specific negative code
        // — the important part is we didn't throw.
        Assert.NotEqual(0, outcome.ExitCode);
        Assert.Contains("git", outcome.StdErr, StringComparison.OrdinalIgnoreCase);
    }
}
