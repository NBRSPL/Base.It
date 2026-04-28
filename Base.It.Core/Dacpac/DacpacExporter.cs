using Base.It.Core.Models;

namespace Base.It.Core.Dacpac;

/// <summary>
/// Writes a captured SQL object into a DACPAC / SSDT-shaped folder so the
/// change is ready to be committed into a <c>.sqlproj</c> source tree.
///
/// Update policy:
///   - If a file with the same name already exists anywhere under the root
///     (case-insensitive), the existing file is <b>updated in place</b>.
///     This preserves whatever folder convention the team's SSDT project
///     already uses (e.g. <c>Procs/</c> vs <c>Stored Procedures/</c>).
///   - Only genuinely new objects are written to a dedicated new-objects
///     folder: <c>Procs2</c>, <c>Functions2</c>, <c>Tables2</c>,
///     <c>Views2</c>, <c>Triggers2</c>. The "2" suffix flags them for
///     review so they can be moved into the project's canonical layout.
///
/// New-object layout:
///   <code>{Root}/{Schema}/{NewTypeFolder}/{Name}.sql</code>
///
/// The exporter writes the exact source definition — no rewriting. SSDT
/// expects <c>CREATE</c> (not <c>ALTER</c>) because the build engine
/// re-emits each object as CREATE from the declared state. If the sync
/// produced an <c>ALTER</c> script, the caller should pass the original
/// source <c>CREATE</c> definition here, not the rewritten one.
/// </summary>
public sealed class DacpacExporter
{
    private readonly DacpacExportOptions _options;

    public DacpacExporter(DacpacExportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public DacpacExportOptions Options => _options;

    /// <summary>
    /// Writes <paramref name="definition"/> to the correct SSDT path —
    /// updating an existing file if one already exists under the root,
    /// otherwise creating a new file under the new-objects folder.
    /// Returns the absolute path of the file written, or <c>null</c> if
    /// the exporter is disabled / unusable.
    /// </summary>
    public string? Export(ObjectIdentifier id, SqlObjectType type, string definition)
    {
        if (!_options.IsUsable) return null;
        if (string.IsNullOrWhiteSpace(definition)) return null;

        var fileName = Sanitize(id.Name) + ".sql";
        var targetPath =
            FindExistingFile(_options.RootFolder, id.Schema, fileName)
            ?? NewObjectPath(id.Schema, type, fileName);

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        // SSDT source trees expect CRLF + UTF-8 BOM by convention.
        File.WriteAllText(targetPath, NormalizeToCrlf(definition), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return targetPath;
    }

    /// <summary>
    /// Returns true if the SSDT tree already contains a <c>.sql</c> file
    /// for this object. Used by the trigger-inline policy in the DACPAC
    /// export step: when a trigger has no existing file, its definition
    /// is embedded in the parent table's file rather than written to a
    /// new <c>Triggers2/</c> file. Returns false when the exporter is
    /// disabled / unusable (no SSDT root configured).
    /// </summary>
    public bool HasExistingFile(ObjectIdentifier id)
    {
        if (!_options.IsUsable) return false;
        var fileName = Sanitize(id.Name) + ".sql";
        return FindExistingFile(_options.RootFolder, id.Schema, fileName) is not null;
    }

    /// <summary>
    /// The relative path within <see cref="DacpacExportOptions.RootFolder"/>
    /// for an object — returns the existing file's path when one is found,
    /// otherwise the new-objects path.
    /// </summary>
    public string RelativePathFor(ObjectIdentifier id, SqlObjectType type)
    {
        var fileName = Sanitize(id.Name) + ".sql";
        var existing = _options.IsUsable
            ? FindExistingFile(_options.RootFolder, id.Schema, fileName)
            : null;
        var absolute = existing ?? NewObjectPath(id.Schema, type, fileName);
        return _options.IsUsable
            ? Path.GetRelativePath(_options.RootFolder, absolute)
            : Path.Combine(Sanitize(id.Schema), NewTypeFolder(type), fileName);
    }

    /// <summary>
    /// Canonical SSDT target folder name for a type — the shape a DACPAC
    /// build expects. Kept for reference / tooling that needs to resolve
    /// the "final" layout; new objects go to <see cref="NewTypeFolder"/>.
    /// </summary>
    public static string TypeFolder(SqlObjectType type) => type switch
    {
        SqlObjectType.Table                 => "Tables",
        SqlObjectType.View                  => "Views",
        SqlObjectType.StoredProcedure       => "Stored Procedures",
        SqlObjectType.ScalarFunction        => "Functions",
        SqlObjectType.InlineTableFunction   => "Functions",
        SqlObjectType.TableValuedFunction   => "Functions",
        SqlObjectType.Trigger               => "Triggers",
        _                                   => "Misc"
    };

    /// <summary>
    /// Folder name used for <b>new</b> objects that don't yet exist anywhere
    /// in the SSDT tree. The "2" suffix flags them for review so a human
    /// can move them into the project's canonical layout.
    /// </summary>
    public static string NewTypeFolder(SqlObjectType type) => type switch
    {
        SqlObjectType.Table                 => "Tables2",
        SqlObjectType.View                  => "Views2",
        SqlObjectType.StoredProcedure       => "Procs2",
        SqlObjectType.ScalarFunction        => "Functions2",
        SqlObjectType.InlineTableFunction   => "Functions2",
        SqlObjectType.TableValuedFunction   => "Functions2",
        SqlObjectType.Trigger               => "Triggers2",
        _                                   => "Misc2"
    };

    private string NewObjectPath(string schema, SqlObjectType type, string fileName)
        => Path.Combine(_options.RootFolder, Sanitize(schema), NewTypeFolder(type), fileName);

    /// <summary>
    /// Searches for an existing <c>.sql</c> file with the given name under
    /// <paramref name="root"/>. Prefers a match scoped to
    /// <c>{root}/{schema}/**</c> first (so same-named objects in different
    /// schemas don't collide), then falls back to a root-wide search for
    /// flat SSDT layouts.
    /// </summary>
    private static string? FindExistingFile(string root, string schema, string fileName)
    {
        try
        {
            if (!Directory.Exists(root)) return null;

            var schemaRoot = Path.Combine(root, Sanitize(schema));
            if (Directory.Exists(schemaRoot))
            {
                var scoped = Directory
                    .EnumerateFiles(schemaRoot, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (scoped is not null) return scoped;
            }

            return Directory
                .EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string Sanitize(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return "_";
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buf = stackalloc char[segment.Length];
        for (int i = 0; i < segment.Length; i++)
            buf[i] = invalid.Contains(segment[i]) ? '_' : segment[i];
        return new string(buf).Trim();
    }

    private static string NormalizeToCrlf(string s)
        => s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
}
