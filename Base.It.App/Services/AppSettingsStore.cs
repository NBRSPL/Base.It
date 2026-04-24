using System.Text.Json;

namespace Base.It.App.Services;

/// <summary>
/// App-wide user-preferences store (separate from the DPAPI connection store).
/// Persists theme choice and any other purely-UI preferences at
/// %LOCALAPPDATA%\Base.It\appsettings.json. Read/write is synchronous and
/// tolerant — a missing or corrupt file returns defaults; a failed save
/// is swallowed (the setting just won't persist across restarts).
/// </summary>
public sealed class AppSettingsStore
{
    public enum ThemePref { Dark, Light, System }

    private readonly string _path;
    private AppSettingsFile _file;

    public AppSettingsStore(string rootFolder)
    {
        Directory.CreateDirectory(rootFolder);
        _path = Path.Combine(rootFolder, "appsettings.json");
        _file = TryLoad() ?? new AppSettingsFile();
    }

    public ThemePref Theme
    {
        get => _file.Theme;
        set { _file.Theme = value; Save(); }
    }

    public bool HasSeenGettingStarted
    {
        get => _file.HasSeenGettingStarted;
        set { _file.HasSeenGettingStarted = value; Save(); }
    }

    /// <summary>
    /// User-chosen backup root. Null/blank means "use the resolved default"
    /// (env var override → C:\DB_Backup → per-user fallback). Persisted
    /// verbatim so an empty string round-trips as "no preference".
    /// </summary>
    public string? BackupRoot
    {
        get => _file.BackupRoot;
        set { _file.BackupRoot = value; Save(); }
    }

    private AppSettingsFile? TryLoad()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettingsFile>(json);
        }
        catch { return null; }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_file, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch { /* best-effort — non-fatal */ }
    }

    private sealed class AppSettingsFile
    {
        public ThemePref Theme { get; set; } = ThemePref.Dark;
        public bool HasSeenGettingStarted { get; set; } = false;
        public string? BackupRoot { get; set; } = null;
    }
}
