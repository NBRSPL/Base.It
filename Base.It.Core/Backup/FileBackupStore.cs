using System.IO.Compression;
using Base.It.Core.Models;

namespace Base.It.Core.Backup;

/// <summary>
/// Distinguishes who an object's backup file represents — used by the
/// backup folder name so a folder full of <c>.sql</c> files reads as
/// "source-side state of PROD" or "destination-side pre-sync state of DEV"
/// at a glance, with no timestamp parsing required.
/// </summary>
public enum BackupRole
{
    /// <summary>The source environment the sync was pulling FROM.</summary>
    Source,
    /// <summary>A destination environment captured BEFORE the sync ran (revert candidate).</summary>
    Target,
    /// <summary>Standalone capture (no sync involved) — manual Backup button.</summary>
    Manual,
}

/// <summary>
/// Writes object definitions to a backup root using a run-grouped layout
/// designed to make the Scripts pane's "load + execute" workflow safe:
///
///   {Root}\{yyyy-MM-dd}\{role}_{env}_{runStamp}\{ObjectType}\{Name}.sql
///
/// The role + environment lead the folder name (so it reads
/// "source_PROD_…" / "destination_DEV_…" at a glance) and the
/// <c>runStamp</c> (HHmmssfff, or a custom label) trails it. One run (one
/// Sync / Batch / Backup click) shares a single stamp, so all its
/// per-role / per-env folders share the same trailing <c>_{stamp}</c>.
/// Each role/env folder contains exactly one file per object — no
/// timestamp suffixes inside the file name — so the Scripts pane can pick
/// a folder and re-execute it without duplicate-object hazards.
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
    /// Returns true when a backup folder for <paramref name="runStamp"/>
    /// already exists under today's date directory — used to validate
    /// user-supplied custom backup names before a run starts. The stamp
    /// now TRAILS the folder name (<c>{role}_{env}_{stamp}</c>), so a
    /// match is "any folder whose name ends with <c>_{runStamp}</c>",
    /// detected across role / env variations (e.g.
    /// <c>source_DEV_before-feature-x</c>). Case-insensitive on
    /// Windows-style filesystems.
    /// </summary>
    public bool IsRunStampInUseToday(string runStamp)
    {
        if (string.IsNullOrWhiteSpace(runStamp)) return false;
        var dateDir = Path.Combine(_root, DateTime.Now.ToString("yyyy-MM-dd"));
        if (!Directory.Exists(dateDir)) return false;
        var suffix = "_" + SanitizeSegment(runStamp);
        try
        {
            return Directory.EnumerateDirectories(dateDir)
                .Select(d => Path.GetFileName(d) ?? "")
                .Any(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

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
        var roleSlug = RoleSlug(role);
        var envSegment  = SanitizeSegment(environment);
        // Role + env lead, stamp trails: "source_PROD_143022456".
        var folderName  = $"{roleSlug}_{envSegment}_{SanitizeSegment(runStamp)}";
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
    /// Write a whole set of objects into ONE consolidated, runnable .sql
    /// script — the "single script instead of a folder of files" backup.
    ///
    ///   {Root}\{yyyy-MM-dd}\{role}_{env}_{runStamp}.sql
    ///
    /// Each object is emitted under a banner comment and terminated with a
    /// <c>GO</c> batch separator so the file can be re-run as-is in SSMS
    /// (CREATE PROCEDURE / VIEW / etc. each require their own batch). The
    /// object order is preserved as given by the caller. Returns the file
    /// path, or null when the set is empty (nothing to write).
    /// </summary>
    public string? WriteScript(
        string runStamp,
        BackupRole role,
        string environment,
        IReadOnlyList<(SqlObjectType Type, ObjectIdentifier Id, string Definition)> objects)
    {
        if (objects is null || objects.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(runStamp)) runStamp = NewRunStamp();

        var date = DateTime.Now.ToString("yyyy-MM-dd");
        var roleSlug = RoleSlug(role);
        var envSegment = SanitizeSegment(environment);
        var stamp = SanitizeSegment(runStamp);
        var dir = Path.Combine(_root, date);
        Directory.CreateDirectory(dir);

        // Role + env lead, stamp trails: "source_PROD_143022456.sql".
        var file = Path.Combine(dir, $"{roleSlug}_{envSegment}_{stamp}.sql");
        int n = 1;
        while (File.Exists(file))
            file = Path.Combine(dir, $"{roleSlug}_{envSegment}_{stamp}_{n++}.sql");

        var sb = new System.Text.StringBuilder(objects.Count * 512);
        sb.Append("-- Base.It backup bundle\n");
        sb.Append($"-- Role: {roleSlug}   Environment: {environment}\n");
        sb.Append($"-- Captured: {DateTime.Now:yyyy-MM-dd HH:mm:ss}   Objects: {objects.Count}\n");
        sb.Append("-- Re-runnable as a single script (GO-separated batches).\n\n");

        foreach (var (type, id, definition) in objects)
        {
            sb.Append("-- ============================================================\n");
            sb.Append($"-- [{id.Schema}].[{id.Name}]  ({type})\n");
            sb.Append("-- ============================================================\n");
            sb.Append(definition.TrimEnd());
            sb.Append('\n');
            sb.Append("GO\n\n");
        }

        File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

    /// <summary>
    /// Folder/file label for a role. "destination" (not "target") is the
    /// user-facing word used across the backup layout.
    /// </summary>
    private static string RoleSlug(BackupRole role) => role switch
    {
        BackupRole.Source => "source",
        BackupRole.Target => "destination",
        _                 => "manual",
    };

    private static string SanitizeSegment(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "_";
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buf = stackalloc char[s.Length];
        for (int i = 0; i < s.Length; i++) buf[i] = invalid.Contains(s[i]) ? '_' : s[i];
        return new string(buf).Trim();
    }
}
