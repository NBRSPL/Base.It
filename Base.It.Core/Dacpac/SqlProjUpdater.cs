using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Base.It.Core.Dacpac;

/// <summary>
/// Adds <c>&lt;Build Include="..." /&gt;</c> entries to the SSDT
/// <c>.sqlproj</c> when a brand-new <c>.sql</c> file is dropped into the
/// project tree by <see cref="DacpacExporter"/>. Without this, a new object
/// written by the tool shows up as an untracked .sql file but the .sqlproj
/// stays unchanged in the git diff — which made it easy to forget to
/// register the file in MSBuild.
///
/// Behaviour:
///  - Searches the configured DACPAC root (and the first level of its
///    subdirectories — some SSDT layouts nest the .sqlproj one level down)
///    for a single <c>.sqlproj</c>. Multiple matches → first one wins.
///  - Reads the project as XML, normalises the include path to be
///    relative to the <c>.sqlproj</c>'s directory, and uses backslashes
///    (the SSDT convention).
///  - Skips the write entirely when an equivalent
///    <c>&lt;Build Include="..." /&gt;</c> entry is already present —
///    matched case-insensitively, with both forward and back-slash forms
///    treated as equal.
///  - When adding, prefers an existing <c>&lt;ItemGroup&gt;</c> that
///    already contains <c>Build</c> entries. Falls back to creating a
///    new <c>&lt;ItemGroup&gt;</c> at the end of the project.
///  - Preserves the file's encoding (UTF-8 BOM is the SSDT default) and
///    forces CRLF line endings on the way out so the diff stays clean.
///
/// All file IO is best-effort: any exception is swallowed and surfaced via
/// the boolean return so a sync run never fails because the .sqlproj
/// touch-up couldn't run.
/// </summary>
public static class SqlProjUpdater
{
    /// <summary>
    /// Ensure <paramref name="sqlFileAbsolutePath"/> is registered as a
    /// <c>&lt;Build Include="..." /&gt;</c> entry in the .sqlproj that
    /// owns <paramref name="rootFolder"/>. Returns true when the project
    /// file was modified (new entry added), false otherwise — including
    /// when the entry already existed, no .sqlproj was found, or the
    /// project file couldn't be parsed.
    /// </summary>
    public static bool EnsureBuildIncludes(string rootFolder, string sqlFileAbsolutePath)
        => EnsureBuildIncludes(rootFolder, new[] { sqlFileAbsolutePath });

    /// <summary>
    /// Batch variant — atomically adds every missing entry in a single
    /// project-file write. Cheaper than calling the single-file overload
    /// in a loop because we parse + serialise the .sqlproj once.
    /// </summary>
    public static bool EnsureBuildIncludes(string rootFolder, IEnumerable<string> sqlFileAbsolutePaths)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder)) return false;

        var sqlproj = FindSqlProj(rootFolder);
        if (sqlproj is null) return false;

        try
        {
            var projectDir = Path.GetDirectoryName(sqlproj)!;

            XDocument doc;
            using (var stream = File.OpenRead(sqlproj))
                doc = XDocument.Load(stream, LoadOptions.PreserveWhitespace);

            var root = doc.Root;
            if (root is null) return false;

            // SSDT projects use the standard MSBuild namespace. Match it
            // dynamically rather than hardcoding so non-namespaced or
            // SDK-style variants both work.
            var ns = root.GetDefaultNamespace();

            // Collect existing Build includes (case-insensitive, slash-agnostic).
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var build in root.Descendants(ns + "Build"))
            {
                var inc = (string?)build.Attribute("Include");
                if (!string.IsNullOrWhiteSpace(inc))
                    existing.Add(NormalizeIncludePath(inc));
            }

            // Build the relative include for each new path; keep only the
            // ones that aren't already registered.
            var toAdd = new List<string>();
            foreach (var abs in sqlFileAbsolutePaths.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                if (!File.Exists(abs)) continue;
                var rel = Path.GetRelativePath(projectDir, abs).Replace('/', '\\');
                if (existing.Add(NormalizeIncludePath(rel)))
                    toAdd.Add(rel);
            }
            if (toAdd.Count == 0) return false;

            // Pick the ItemGroup that already hosts Build entries; fall
            // back to a fresh ItemGroup at the project tail.
            var hostGroup = root
                .Elements(ns + "ItemGroup")
                .FirstOrDefault(ig => ig.Elements(ns + "Build").Any());

            if (hostGroup is null)
            {
                hostGroup = new XElement(ns + "ItemGroup");
                root.Add(hostGroup);
            }

            foreach (var rel in toAdd)
                hostGroup.Add(new XElement(ns + "Build",
                    new XAttribute("Include", rel)));

            // Round-trip via XmlWriter so namespace declarations and
            // formatting stay close to the original. Force CRLF + UTF-8
            // BOM (SSDT default) so the diff is purely additive.
            var settings = new XmlWriterSettings
            {
                Indent           = true,
                IndentChars      = "  ",
                NewLineChars     = "\r\n",
                NewLineHandling  = NewLineHandling.Replace,
                Encoding         = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                OmitXmlDeclaration = false,
            };

            // Write to a temp file then move into place — keeps the
            // .sqlproj intact if the disk fills up mid-write.
            var tmp = sqlproj + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var xw = XmlWriter.Create(fs, settings))
                doc.Save(xw);
            File.Copy(tmp, sqlproj, overwrite: true);
            File.Delete(tmp);

            return true;
        }
        catch
        {
            // Swallow — caller treats false as "no project change made".
            return false;
        }
    }

    private static string? FindSqlProj(string root)
    {
        try
        {
            var top = Directory.EnumerateFiles(root, "*.sqlproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (top is not null) return top;

            // Some teams keep the .sqlproj one folder down (e.g. inside a
            // 'src/MyProj/' subdir under the repo root). Look one level
            // deep before giving up — we don't recurse further to avoid
            // accidentally matching an unrelated project elsewhere on disk.
            foreach (var sub in Directory.EnumerateDirectories(root))
            {
                var here = Directory.EnumerateFiles(sub, "*.sqlproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (here is not null) return here;
            }
        }
        catch { }
        return null;
    }

    private static string NormalizeIncludePath(string s)
        => s.Trim().Replace('/', '\\');
}
