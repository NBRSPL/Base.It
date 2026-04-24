using System.Diagnostics;

namespace Base.It.Core.Dacpac;

/// <summary>
/// Result of a git operation. <see cref="ExitCode"/> of 0 means success;
/// anything else indicates failure — the caller decides whether to surface
/// the <see cref="StdErr"/> to the user.
/// </summary>
public sealed record GitOutcome(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// Lightweight wrapper over the <c>git</c> CLI. The only operations we
/// perform are read-only state checks, <c>checkout -b</c>, and
/// <c>add</c> — deliberately NEVER <c>commit</c>, NEVER <c>push</c>, and
/// NEVER any remote interaction. Preparing a branch is explicitly where
/// this class stops; the user commits and raises a PR themselves.
/// </summary>
public class GitStager
{
    private readonly string _workingDir;
    private readonly string _git;

    /// <param name="workingDir">Folder inside a git working copy.</param>
    /// <param name="gitExecutable">Override for tests; defaults to <c>git</c> on PATH.</param>
    public GitStager(string workingDir, string gitExecutable = "git")
    {
        _workingDir = workingDir ?? throw new ArgumentNullException(nameof(workingDir));
        _git        = gitExecutable;
    }

    /// <summary>Creates and checks out a new branch. Fails if the branch already exists.</summary>
    public Task<GitOutcome> CreateBranchAsync(string branchName, CancellationToken ct = default)
        => RunAsync(new[] { "checkout", "-b", branchName }, ct);

    /// <summary>Stages specific files (paths relative to the working dir or absolute).</summary>
    public Task<GitOutcome> StageAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        if (paths is null || paths.Count == 0)
            return Task.FromResult(new GitOutcome(0, "", "nothing to stage"));
        var args = new List<string>(paths.Count + 1) { "add", "--" };
        args.AddRange(paths);
        return RunAsync(args, ct);
    }

    /// <summary>True when <paramref name="path"/> is inside a git working tree (<c>git rev-parse</c>).</summary>
    public async Task<bool> IsInsideWorkingCopyAsync(CancellationToken ct = default)
    {
        var r = await RunAsync(new[] { "rev-parse", "--is-inside-work-tree" }, ct).ConfigureAwait(false);
        return r.Ok && r.StdOut.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a default branch name like <c>drift/20260422-1507</c>. The
    /// caller owns the prefix so different flows can name differently.
    /// </summary>
    public static string TimestampedBranch(string prefix)
        => prefix + DateTime.Now.ToString("yyyyMMdd-HHmm");

    /// <summary>Low-level runner, exposed for tests.</summary>
    public virtual async Task<GitOutcome> RunAsync(IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName  = _git,
            WorkingDirectory = _workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try { proc.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // git not found on PATH — surface a clean message instead of
            // the raw Win32 error.
            return new GitOutcome(127, "", $"git not found on PATH: {ex.Message}");
        }

        var stdOut = proc.StandardOutput.ReadToEndAsync();
        var stdErr = proc.StandardError.ReadToEndAsync();
        try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new GitOutcome(proc.ExitCode, await stdOut.ConfigureAwait(false), await stdErr.ConfigureAwait(false));
    }
}
