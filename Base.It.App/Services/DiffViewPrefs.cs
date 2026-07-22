using System;
using System.IO;
using System.Text.Json;

namespace Base.It.App.Services;

/// <summary>
/// Persisted view option for the shared diff pane: "Ignore spaces &amp; tabs".
/// Stored as a tiny JSON file under the per-user app-data folder so the
/// choice survives closing one preview, opening another, AND restarting
/// the app. (Line numbers are always shown, so there's no pref for them.)
///
/// Static + file-backed by design: the diff views are created ad-hoc for
/// every preview / Compare tab, so there's nowhere to inject a service.
/// The value is cached after first load; the setter writes through to
/// disk best-effort (a failed write just means the pref isn't remembered,
/// never a crash).
/// </summary>
public static class DiffViewPrefs
{
    private sealed record Prefs(bool IgnoreWhitespace);

    private static readonly object _gate = new();
    private static Prefs? _cache;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Base.It", "diff-view-prefs.json");

    private static Prefs Load()
    {
        lock (_gate)
        {
            if (_cache is not null) return _cache;
            try
            {
                var path = FilePath;
                if (File.Exists(path))
                    _cache = JsonSerializer.Deserialize<Prefs>(File.ReadAllText(path));
            }
            catch { /* corrupt / unreadable → defaults */ }
            _cache ??= new Prefs(IgnoreWhitespace: false);
            return _cache;
        }
    }

    private static void Save(Prefs p)
    {
        lock (_gate)
        {
            _cache = p;
            try
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(p));
            }
            catch { /* best-effort */ }
        }
    }

    public static bool IgnoreWhitespace
    {
        get => Load().IgnoreWhitespace;
        set => Save(Load() with { IgnoreWhitespace = value });
    }
}
