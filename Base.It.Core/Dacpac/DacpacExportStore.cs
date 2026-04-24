using System.Text.Json;

namespace Base.It.Core.Dacpac;

/// <summary>
/// File-backed store for <see cref="DacpacExportOptions"/>. JSON on disk
/// so the user can hand-edit if they want. Missing file = "Disabled".
/// Atomic-ish writes via temp-file-then-rename.
/// </summary>
public sealed class DacpacExportStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    public DacpacExportStore(string path) { _path = path ?? throw new ArgumentNullException(nameof(path)); }
    public string Path => _path;

    public async Task<DacpacExportOptions> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return DacpacExportOptions.Disabled;
        try
        {
            await using var fs = File.OpenRead(_path);
            var opts = await JsonSerializer.DeserializeAsync<DacpacExportOptions>(fs, JsonOpts, ct).ConfigureAwait(false);
            return opts ?? DacpacExportOptions.Disabled;
        }
        catch
        {
            // Corrupt config should never break the app — fall back to disabled.
            return DacpacExportOptions.Disabled;
        }
    }

    public async Task SaveAsync(DacpacExportOptions options, CancellationToken ct = default)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, options, JsonOpts, ct).ConfigureAwait(false);
        }
        File.Move(tmp, _path, overwrite: true);
    }
}
