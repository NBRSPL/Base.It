using System.IO.Compression;
using Base.It.Core.Models;

namespace Base.It.Core.Backup;

/// <summary>
/// Distinguishes who an object's backup file represents — used by the
/// backup folder name so a folder full of <c>.sql</c> files reads as
/// "source-side state of PROD" or "target-side pre-sync state of DEV"
/// at a glance, with no timestamp parsing required.
/// </summary>
public enum BackupRole
{
    /// <summary>The source environment the sync was pulling FROM.</summary>
    Source,
    /// <summary>A target environment captured BEFORE the sync ran (revert candidate).</summary>
    Target,
    /// <summary>Standalone capture (no sync involved) — manual Backup button.</summary>
    Manual,
}

/// <summary>
/// Writes object definitions to a backup root using a run-grouped layout
/// designed to make the Scripts pane's "load + execute" workflow safe:
///
///   {Root}\{yyyy-MM-dd}\{runStamp}_{role}_{env}\{ObjectType}\{Name}.sql
///
/// One run (one Sync / Batch / Backup click) shares a single
/// <c>runStamp</c> (HHmmssfff). All files for that run land under
/// per-role / per-env folders named with that stamp. Each role/env
/// folder contains exactly one file per object — no timestamp suffixes
/// inside the file name — so the Scripts pane can pick a folder and
/// re-execute it without duplicate-object hazards.
///
/// Cross-run uniqueness is handled by the stamp; within-run name
/// collisions (rare: same object captured twice in the same run) get a
/// trailing <c>_2</c>, <c>_3</c> suffix so nothing is overwritten.
/// </summary>
public sealed class FileBackupStore
{
    private string _root;

    public FileBackupStore(string root) { _root = root; Directory.CreateDirectory(_root); }
    public string Root => _root;

    public void SetRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Backup folder path is required.", nameof(root));
        Directory.CreateDirectory(root);
        _root = root;
    }

    /// <summary>
    /// Generate a fresh run-stamp (HHmmssfff). Callers should generate
    /// one at the start of an operation and pass it to every
    /// <see cref="WriteObject"/> call in that operation so all artifacts
    /// land in the same run-folder.
    /// </summary>
    public static string NewRunStamp() => DateTime.Now.ToString("HHmmssfff");

    /// <summary>
    /// Write a single object's definition to the run-grouped layout.
    /// </summary>
    public string WriteObject(
        string runStamp,
        BackupRole role,
        string environment,
        SqlObjectType type,
        ObjectIdentifier id,
        string definition)
    {
        if (string.IsNullOrWhiteSpace(runStamp))
            runStamp = NewRunStamp();

        var date = DateTime.Now.ToString("yyyy-MM-dd");
        var roleSlug = role switch
        {
            BackupRole.Source => "source",
            BackupRole.Target => "target",
            _                 => "manual",
        };
        var envSegment  = SanitizeSegment(environment);
        var folderName  = $"{runStamp}_{roleSlug}_{envSegment}";
        var typeSegment = SanitizeSegment(type.ToString());
        var dir = Path.Combine(_root, date, folderName, typeSegment);
        Directory.CreateDirectory(dir);

        // Filename = the object's own identifier (schema kept only when
        // it isn't the default 'dbo'). No timestamp — the run-folder is
        // already unique, so the file name stays clean.
        var nameSegment = SanitizeSegment(
            string.Equals(id.Schema, "dbo", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(id.Schema)
                ? id.Name
                : $"{id.Schema}.{id.Name}");

        var file = Path.Combine(dir, $"{nameSegment}.sql");
        // Same-run, same-object collisions only happen if the caller
        // captures one object twice in one run. Defensive: never
        // overwrite an existing file.
        int n = 1;
        while (File.Exists(file))
            file = Path.Combine(dir, $"{nameSegment}_{n++}.sql");

        File.WriteAllText(file, definition);
        return file;
    }

    /// <summary>
    /// Packages all backup files for a batch run into a single zip under
    /// today's date folder. Preserves the
    /// <c>{runStamp}_{role}_{env}\{type}\{name}.sql</c> structure inside
    /// the archive. Named uniquely with a millisecond timestamp — never
    /// overwrites an existing zip.
    /// </summary>
    public string CreateBatchZip(string zipName, IEnumerable<string> files)
    {
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        var dateRoot = Path.Combine(_root, date);
        Directory.CreateDirectory(dateRoot);

        var zipPath = Path.Combine(dateRoot, zipName);
        int n = 1;
        while (File.Exists(zipPath))
            zipPath = Path.Combine(dateRoot, Path.GetFileNameWithoutExtension(zipName) + $"_{n++}.zip");

        var unique = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var f in unique)
        {
            var entry = f.StartsWith(dateRoot, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(dateRoot, f)
                : Path.GetFileName(f);
            archive.CreateEntryFromFile(f, entry);
        }
        return zipPath;
    }

    /// <summary>
    /// Packages a small set of files into a zip in the same run folder
    /// as the first input. Never deletes or overwrites an existing zip.
    /// </summary>
    public string ZipFiles(string zipName, params string[] files)
    {
        string zipDir = files.Length > 0 && !string.IsNullOrEmpty(Path.GetDirectoryName(files[0]))
            ? Path.GetDirectoryName(files[0])!
            : _root;
        Directory.CreateDirectory(zipDir);

        var zipPath = Path.Combine(zipDir, zipName);
        int n = 1;
        while (File.Exists(zipPath))
            zipPath = Path.Combine(zipDir, Path.GetFileNameWithoutExtension(zipName) + $"_{n++}.zip");

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var f in files)
            if (File.Exists(f))
                archive.CreateEntryFromFile(f, Path.GetFileName(f));
        return zipPath;
    }

    private static string SanitizeSegment(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "_";
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buf = stackalloc char[s.Length];
        for (int i = 0; i < s.Length; i++) buf[i] = invalid.Contains(s[i]) ? '_' : s[i];
        return new string(buf).Trim();
    }
}
