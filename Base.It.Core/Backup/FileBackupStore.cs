using System.IO.Compression;
using Base.It.Core.Models;

namespace Base.It.Core.Backup;

/// <summary>
/// Writes object definitions to a backup root using a date/env/objectType layout.
///
///   {Root}\{yyyy-MM-dd}\{env}\{ObjectType}\{Name}_{HHmmssfff}.sql
///
/// Files are named after the SQL object itself (schema prefix stripped — the
/// default 'dbo' adds no information) with a millisecond timestamp suffix so
/// repeated captures never overwrite each other.
/// </summary>
public sealed class FileBackupStore
{
    private string _root;

    public FileBackupStore(string root) { _root = root; Directory.CreateDirectory(_root); }
    public string Root => _root;

    /// <summary>
    /// Re-point the store at a different folder at runtime (used when the
    /// user changes the backup location in Settings). Creates the folder
    /// if it doesn't exist. Throws if the path is blank or we can't create
    /// the directory — the caller surfaces the error to the user.
    /// Existing files at the old location are NOT moved or deleted; the
    /// switch is only for future writes.
    /// </summary>
    public void SetRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Backup folder path is required.", nameof(root));
        Directory.CreateDirectory(root);
        _root = root;
    }

    public string WriteObject(string environment, SqlObjectType type, ObjectIdentifier id, string definition)
    {
        var now  = DateTime.Now;
        var date = now.ToString("yyyy-MM-dd");
        var time = now.ToString("HHmmssfff");

        // Folder: {date}/{env}/{ObjectType}/   — env and object type are
        // categorical groupings, not part of the file name.
        var envSegment    = SanitizeSegment(environment);
        var typeSegment   = SanitizeSegment(type.ToString());
        var dir = Path.Combine(_root, date, envSegment, typeSegment);
        Directory.CreateDirectory(dir);

        // Filename starts with the object's own name; schema is dropped
        // unless it's something other than the default 'dbo'.
        var nameSegment = SanitizeSegment(
            string.Equals(id.Schema, "dbo", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(id.Schema)
                ? id.Name
                : $"{id.Schema}.{id.Name}");

        var file = Path.Combine(dir, $"{nameSegment}_{time}.sql");
        // Ms stamp effectively prevents collisions; belt-and-braces counter
        // suffix in the pathological same-ms case rather than overwrite.
        int n = 1;
        while (File.Exists(file))
            file = Path.Combine(dir, $"{nameSegment}_{time}_{n++}.sql");

        File.WriteAllText(file, definition);
        return file;
    }

    /// <summary>
    /// Packages all backup files for a batch run into a single zip under
    /// today's date folder, preserving the <c>{env}\{type}\{name}.sql</c>
    /// structure inside the archive. Named uniquely with a millisecond
    /// timestamp — never overwrites an existing zip.
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

        // Materialize the distinct, existing file list so the archive sees
        // each backup at most once even if source+target duplicates slip in.
        var unique = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var f in unique)
        {
            // Entry path: relative to the date folder, falls back to the
            // plain filename if the file lives outside the backup root
            // (defensive — shouldn't happen in practice).
            var entry = f.StartsWith(dateRoot, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(dateRoot, f)
                : Path.GetFileName(f);
            archive.CreateEntryFromFile(f, entry);
        }
        return zipPath;
    }

    /// <summary>
    /// Packages a set of already-written backup files into a single zip in the
    /// same date/object folder. Never deletes or overwrites an existing zip;
    /// a unique millisecond stamp is part of the zip name.
    /// </summary>
    public string ZipFiles(string zipName, params string[] files)
    {
        // Put the zip next to the source files when possible so related
        // artifacts stay together.
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
