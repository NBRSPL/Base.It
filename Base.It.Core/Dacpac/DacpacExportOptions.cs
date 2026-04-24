namespace Base.It.Core.Dacpac;

/// <summary>
/// User configuration for exporting synced objects to a DACPAC / SSDT-shaped
/// folder. The exporter writes one .sql file per object using the
/// conventional SSDT layout. Persisted as JSON — safe to hand-edit.
/// </summary>
public sealed record DacpacExportOptions(
    /// <summary>Master switch. False = no export, no git touch, no side effects.</summary>
    bool   Enabled,
    /// <summary>
    /// Root folder where .sql files are written. For an SSDT project this
    /// is usually the folder containing the <c>.sqlproj</c>. May or may
    /// not be inside a Git working copy; git staging is opt-in below.
    /// </summary>
    string RootFolder,
    /// <summary>
    /// When true, the exporter (after writing files) runs
    /// <c>git checkout -b &lt;BranchPrefix&gt;&lt;yyyyMMdd-HHmm&gt;</c> and
    /// <c>git add</c> on every changed file. It NEVER commits, never pushes,
    /// and never raises a PR — the user reviews and commits manually.
    /// </summary>
    bool   StageInGit,
    /// <summary>Branch name prefix when <see cref="StageInGit"/> is true. Default <c>drift/</c>.</summary>
    string BranchPrefix)
{
    public static DacpacExportOptions Disabled =>
        new(Enabled: false, RootFolder: "", StageInGit: false, BranchPrefix: "drift/");

    public bool IsUsable =>
        Enabled && !string.IsNullOrWhiteSpace(RootFolder) && Directory.Exists(RootFolder);
}
