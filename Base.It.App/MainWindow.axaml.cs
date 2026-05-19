using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Base.It.App.Services;
using Base.It.App.ViewModels;
using Base.It.App.Views;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;

namespace Base.It.App;

public partial class MainWindow : AppWindow
{
    private NavigationView _nav = null!;
    private Frame _host = null!;
    private Border _groupSelector = null!;
    private TextBlock _themeGlyph = null!;
    // Find overlay is no longer triggered by Ctrl+F (its filter-mirroring
    // behaviour was confusing). Border ref is kept so the dead Esc-to-
    // close handler can still hide it if it's ever shown via other code.
    private Border _findOverlay = null!;

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _nav  = this.FindControl<NavigationView>("Nav")!;
        _host = this.FindControl<Frame>("Host")!;
        _groupSelector = this.FindControl<Border>("GroupSelector")!;
        _themeGlyph = this.FindControl<TextBlock>("ThemeGlyph")!;
        _findOverlay = this.FindControl<Border>("FindOverlay")!;
        _nav.SelectionChanged += OnNavSelectionChanged;

        // Window-wide Ctrl+F: like a browser's find. Routes to whatever
        // view is currently in the Frame, as long as it implements
        // ISupportsFind. The window-level handler runs even when focus
        // is on the nav pane, the title bar, or any nested control —
        // so the user never has to think about which input to click first.
        AddHandler(KeyDownEvent, OnGlobalKeyDown, Avalonia.Interactivity.RoutingStrategies.Bubble | Avalonia.Interactivity.RoutingStrategies.Tunnel);

        DataContext = new MainWindowViewModel();

        // Apply persisted theme preference now that the Application is up.
        Vm.Services.Theme.ApplyFromSettings();
        Vm.Services.Theme.ThemeChanged += UpdateThemeGlyph;
        UpdateThemeGlyph();

        // Version pill: belt-and-braces. The XAML binding to
        // Services.Updater.CurrentVersion should resolve, but compiled
        // bindings can be finicky across NavigationView.PaneFooter's
        // NameScope and we want the pill to show *something* on every
        // launch. Set the text imperatively here as a fallback.
        UpdateVersionPillText();

        Vm.NavigateToCompareRequested += () =>
        {
            if (_nav.SelectedItem is NavigationViewItem { Tag: "Compare" }) return;
            SelectByTag("Compare");
        };
        Vm.NavigateToBatchRequested += () => SelectByTag("Batch");
        Vm.NavigateToTagRequested   += SelectByTag;

        // VM asked to open a Batch in a NEW Window (Send Changes → "Open in new
        // window" path). Each new BatchWindow owns its own BatchViewModel
        // instance so the main tab's pending work stays untouched. Window is
        // non-modal (Show, not ShowDialog) so the user can flip between the
        // new window and the main app freely.
        Vm.OpenBatchInNewWindowRequested += batchVm =>
        {
            var win = new BatchWindow { DataContext = batchVm };
            win.Show(this);
        };

        Closing += (_, _) =>
        {
            _ = Vm.Watch.ShutdownAsync();
        };

        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.TitleBarHitTestType = TitleBarHitTestType.Complex;

        Opened += (_, _) =>
        {
            ClampToScreen();
            // First launch: land on Home so the user has guided steps. If
            // they already have connections, Home is still the best landing
            // (quick dashboard) — users can then click through to any tab.
            SelectByTag("Home");
            _ = RunStartupUpdateCheckAsync();
        };
    }

    /// <summary>
    /// Run an update probe on every app launch. Surfaces an info toast
    /// ("Checking for updates…") while the network call is in flight so
    /// the user always sees the system working. After the check:
    ///   - UpToDate → flip the same toast to "You're on the latest version"
    ///                (auto-dismisses on the standard timer).
    ///   - Available → dismiss the toast and open a small confirm dialog
    ///                 asking "v{X} is available — install now?".
    ///                 Yes downloads + applies + restarts; No closes the
    ///                 dialog and leaves the app running (user can still
    ///                 trigger it later from Settings → Updates).
    ///   - Failed / dev build / offline → dismiss the toast silently so
    ///                 a flaky connection doesn't pester the user.
    /// </summary>
    private async Task RunStartupUpdateCheckAsync()
    {
        var updater = Vm.Services.Updater;
        var toasts  = Vm.Services.Toasts;

        // Don't show anything in dev / non-Velopack runs — there's no
        // installed app to update.
        if (!updater.IsInstalled) return;

        // Update the timestamp regardless of result so other code that
        // reads it (Settings → Updates "Last checked") stays accurate.
        Vm.Services.AppSettings.LastUpdateCheckUtc = DateTime.UtcNow;

        Services.ToastItem? checkingToast = null;
        try
        {
            checkingToast = toasts.Info("Checking for updates…");

            await updater.CheckForUpdatesAsync().ConfigureAwait(true);

            switch (updater.State)
            {
                case Services.UpdateState.UpToDate:
                    // Replace the in-flight toast with a brief success so the
                    // user knows the check finished and they're current.
                    if (checkingToast is not null) toasts.Dismiss(checkingToast);
                    var current = string.IsNullOrWhiteSpace(updater.CurrentVersion)
                        ? "the latest version"
                        : $"v{updater.CurrentVersion}";
                    toasts.Success("You're up to date", $"Running {current}.");
                    break;

                case Services.UpdateState.Available:
                    // Drop the "checking" toast — the dialog takes over from here.
                    if (checkingToast is not null) toasts.Dismiss(checkingToast);
                    var latest = string.IsNullOrWhiteSpace(updater.LatestVersion)
                        ? "A newer version"
                        : $"Base.It v{updater.LatestVersion}";
                    var ok = await Services.ConfirmDialog.AskAsync(
                        title:       "Update available",
                        message:     $"{latest} is available. Install it now? The app will download the update and restart.",
                        primaryText: "Update now",
                        cancelText:  "Later");
                    if (ok) await ApplyUpdateInteractivelyAsync();
                    break;

                default:
                    // Failed / Checking-still / network error → silent.
                    if (checkingToast is not null) toasts.Dismiss(checkingToast);
                    break;
            }
        }
        catch
        {
            // Best-effort — never let the update probe break startup.
            if (checkingToast is not null) toasts.Dismiss(checkingToast);
        }
    }

    /// <summary>
    /// Action wired to the "Update now" button on the update toast.
    /// Downloads the pending update with a progress toast, then applies +
    /// restarts. On failure shows an error toast and leaves the app
    /// running so the user can retry from Settings → Updates.
    /// </summary>
    private async Task ApplyUpdateInteractivelyAsync()
    {
        var updater = Vm.Services.Updater;
        var toasts  = Vm.Services.Toasts;
        try
        {
            toasts.Info("Downloading update", "This may take a minute on a slow connection.");
            await updater.DownloadAsync();
            if (updater.State == Services.UpdateState.ReadyToApply)
            {
                // ApplyAndRestart never returns — the loader swaps the
                // installed app and re-launches.
                updater.ApplyAndRestart();
            }
            else
            {
                toasts.Error("Update failed", string.IsNullOrWhiteSpace(updater.LastError) ? "Unknown error." : updater.LastError);
            }
        }
        catch (Exception ex)
        {
            toasts.Error("Update failed", ex.Message);
        }
    }

    private void SelectByTag(string tag)
    {
        foreach (var mi in _nav.MenuItems)
            if (mi is NavigationViewItem { Tag: string t } item && t == tag)
            { _nav.SelectedItem = item; return; }
        foreach (var mi in _nav.FooterMenuItems)
            if (mi is NavigationViewItem { Tag: string t } item && t == tag)
            { _nav.SelectedItem = item; return; }
    }

    private void OnToggleTheme(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Vm.Services.Theme.Toggle();
    }

    /// <summary>
    /// Version pill in the pane footer → navigate to Settings. The user
    /// can then scroll to the "Updates" expander. Kept simple: the pill
    /// is a shortcut, not a deep-link with state hand-off.
    /// </summary>
    private void OnVersionPillClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SelectByTag("Settings");
    }

    /// <summary>
    /// Sets the version pill's text from UpdaterService.CurrentVersion
    /// imperatively as a fallback to the XAML binding. The binding path
    /// (Services.Updater.CurrentVersion) flows across the NavigationView.
    /// PaneFooter NameScope which can interact oddly with compiled
    /// bindings; this guarantees the pill always shows a number.
    /// </summary>
    private void UpdateVersionPillText()
    {
        var tb = this.FindControl<TextBlock>("VersionPillText");
        if (tb is null) return;
        var v = Vm.Services.Updater?.CurrentVersion;
        tb.Text = string.IsNullOrWhiteSpace(v) ? "version" : $"v{v}";
    }

    private void UpdateThemeGlyph()
    {
        if (_themeGlyph is null) return;
        _themeGlyph.Text = Vm.Services.Theme.Current switch
        {
            AppSettingsStore.ThemePref.Dark   => "☾",
            AppSettingsStore.ThemePref.Light  => "☀",
            _                                 => "◐",
        };
    }

    private void ClampToScreen()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var wa = screen.WorkingArea;
        double scale = screen.Scaling;

        double maxW = (wa.Width  / scale) - 40;
        double maxH = (wa.Height / scale) - 60;

        if (Width  > maxW) Width  = maxW;
        if (Height > maxH) Height = maxH;
        Position = new PixelPoint(
            wa.X + (int)((wa.Width  - Width  * scale) / 2),
            wa.Y + (int)((wa.Height - Height * scale) / 2));
    }

    /// <summary>
    /// Global Ctrl+F handler. Asks the active view to put keyboard focus
    /// on its own visible filter textbox. The old behaviour opened a
    /// shadow overlay that silently mirrored the page's filter property,
    /// which made it look like there were two separate "search" inputs
    /// when really both were editing the same backing field — confusing.
    /// The overlay is kept in the XAML for now but no longer triggered.
    /// </summary>
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key != Key.F || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (_host?.Content is ISupportsFind find && find.FocusFindBox())
            e.Handled = true;
    }

    // The overlay-based find handlers below are kept callable from XAML
    // (the FindOverlay Border + FindBox TextBox + close button reference
    // them) but Ctrl+F no longer triggers ShowFindOverlay, so the overlay
    // is effectively dead UI. They're left in place to avoid a churn in
    // MainWindow XAML; a future cleanup can drop the overlay entirely.

    private void OnFindBoxTextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        // No-op now — Ctrl+F focuses the page's filter textbox directly.
    }

    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _findOverlay is not null)
        {
            _findOverlay.IsVisible = false;
            e.Handled = true;
        }
    }

    private void OnFindClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_findOverlay is not null) _findOverlay.IsVisible = false;
        e.Handled = true;
    }

    private void OnNavSelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;

        // Fetch dock visibility + pinned state per page:
        //   - Compare: visible + expanded by default. This is the page
        //     fetch is for, so the bar is the primary action surface.
        //   - Sync / Batch / Watch / Query: visible but collapsed by
        //     default. The user can pop it open with one click when
        //     they want to fetch something into Compare from elsewhere.
        //   - Settings / Home: hidden — nothing to fetch here.
        var fetchHidden = tag == "Settings" || tag == "Home";
        Vm.FetchDock.IsVisible  = !fetchHidden;
        Vm.FetchDock.IsExpanded = tag == "Compare";
        // Settings manages groups directly; Home shows its own group summary.
        _groupSelector.IsVisible = tag != "Settings" && tag != "Home";

        switch (tag)
        {
            case "Home":    Vm.Home.Refresh();             break;
            case "Compare": Vm.Compare.ReloadDatabases();  break;
            case "Sync":    Vm.Sync.Reload();              break;
            case "Batch":   Vm.Batch.Reload();             break;
            case "Scripts": Vm.Scripts.Reload();           break;
            case "Query":   Vm.Query.Reload();             break;
            case "Watch":   _ = Vm.Watch.InitializeAsync();break;
            case "Snapshots": Vm.Snapshots.Reload(); break;
            case "Settings": Vm.Settings.LoadCommand.Execute(null); break;
        }

        Control view = tag switch
        {
            "Home"      => new HomeView      { DataContext = Vm.Home      },
            "Compare"   => new CompareView   { DataContext = Vm.Compare   },
            "Sync"      => new SyncView      { DataContext = Vm.Sync      },
            "Batch"     => new BatchView     { DataContext = Vm.Batch     },
            "Scripts"   => new ScriptsView   { DataContext = Vm.Scripts   },
            "Query"     => new QueryView     { DataContext = Vm.Query     },
            "Watch"     => new WatchView     { DataContext = Vm.Watch     },
            "Snapshots" => new SnapshotsView { DataContext = Vm.Snapshots },
            "Settings"  => new SettingsView  { DataContext = Vm.Settings  },
            _           => new TextBlock { Text = "?" }
        };
        _host.Content = view;
    }
}
