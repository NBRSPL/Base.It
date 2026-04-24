using Avalonia;
using Avalonia.Styling;

namespace Base.It.App.Services;

/// <summary>
/// Flips the Avalonia Application theme variant at runtime and persists the
/// choice via <see cref="AppSettingsStore"/>. All our dynamic brushes are
/// wired to the Light / Dark ResourceDictionaries in ThemeResources.axaml,
/// so changing <see cref="Application.RequestedThemeVariant"/> is enough to
/// repaint the entire UI.
/// </summary>
public sealed class ThemeService
{
    private readonly AppSettingsStore _settings;

    public event Action? ThemeChanged;

    public AppSettingsStore.ThemePref Current => _settings.Theme;

    public ThemeService(AppSettingsStore settings)
    {
        _settings = settings;
    }

    /// <summary>Applies the persisted preference to the running Application.</summary>
    public void ApplyFromSettings()
    {
        Apply(_settings.Theme);
    }

    public void Set(AppSettingsStore.ThemePref pref)
    {
        _settings.Theme = pref;
        Apply(pref);
        ThemeChanged?.Invoke();
    }

    public void Toggle()
    {
        // System flips to Dark on toggle — keeps behaviour deterministic.
        var next = Current switch
        {
            AppSettingsStore.ThemePref.Dark => AppSettingsStore.ThemePref.Light,
            _                               => AppSettingsStore.ThemePref.Dark,
        };
        Set(next);
    }

    private static void Apply(AppSettingsStore.ThemePref pref)
    {
        var app = Application.Current;
        if (app is null) return;
        app.RequestedThemeVariant = pref switch
        {
            AppSettingsStore.ThemePref.Dark   => ThemeVariant.Dark,
            AppSettingsStore.ThemePref.Light  => ThemeVariant.Light,
            _                                 => ThemeVariant.Default,
        };
    }
}
