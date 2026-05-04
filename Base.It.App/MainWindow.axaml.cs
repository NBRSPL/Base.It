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
    private Border _findOverlay = null!;
    private TextBox _findBox = null!;
    private bool _suppressFindEcho;

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _nav  = this.FindControl<NavigationView>("Nav")!;
        _host = this.FindControl<Frame>("Host")!;
        _groupSelector = this.FindControl<Border>("GroupSelector")!;
        _themeGlyph = this.FindControl<TextBlock>("ThemeGlyph")!;
        _findOverlay = this.FindControl<Border>("FindOverlay")!;
        _findBox = this.FindControl<TextBox>("FindBox")!;
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

        Vm.NavigateToCompareRequested += () =>
        {
            if (_nav.SelectedItem is NavigationViewItem { Tag: "Compare" }) return;
            SelectByTag("Compare");
        };
        Vm.NavigateToBatchRequested += () => SelectByTag("Batch");
        Vm.NavigateToTagRequested   += SelectByTag;

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
        };
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
    /// Global Ctrl+F handler. Opens (or closes) the find overlay — a
    /// floating popup at the top-right of the window. Browser-style:
    /// the find UI is transient chrome the window owns, not a permanent
    /// input baked into each page.
    /// </summary>
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key != Key.F || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (_host?.Content is not ISupportsFind) return;
        ShowFindOverlay();
        e.Handled = true;
    }

    /// <summary>
    /// Show the find overlay and pre-populate it with the active view's
    /// current filter. Focuses + selects the textbox so a fresh
    /// keystroke replaces whatever's in there.
    /// </summary>
    private void ShowFindOverlay()
    {
        if (_findOverlay is null || _findBox is null) return;
        if (_host?.Content is not ISupportsFind find) return;

        _suppressFindEcho = true;
        try { _findBox.Text = find.CurrentFindText; }
        finally { _suppressFindEcho = false; }

        _findOverlay.IsVisible = true;
        _findBox.Focus();
        _findBox.SelectAll();
    }

    /// <summary>
    /// Close the find overlay AND clear the active view's filter — the
    /// browser pattern. If the user wants the filter to stick, they can
    /// just leave the overlay open (it doesn't steal focus once they've
    /// clicked back into the page).
    /// </summary>
    private void HideFindOverlay()
    {
        if (_findOverlay is null) return;
        _findOverlay.IsVisible = false;

        if (_host?.Content is ISupportsFind find)
            find.ApplyFind(string.Empty);

        if (_findBox is not null)
        {
            _suppressFindEcho = true;
            try { _findBox.Text = string.Empty; }
            finally { _suppressFindEcho = false; }
        }
    }

    private void OnFindBoxTextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (_suppressFindEcho) return;
        if (sender is not TextBox tb) return;
        if (_host?.Content is ISupportsFind find)
            find.ApplyFind(tb.Text);
    }

    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideFindOverlay();
            e.Handled = true;
        }
    }

    private void OnFindClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        HideFindOverlay();
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
            case "Settings": Vm.Settings.LoadCommand.Execute(null); break;
        }

        Control view = tag switch
        {
            "Home"     => new HomeView     { DataContext = Vm.Home     },
            "Compare"  => new CompareView  { DataContext = Vm.Compare  },
            "Sync"     => new SyncView     { DataContext = Vm.Sync     },
            "Batch"    => new BatchView    { DataContext = Vm.Batch    },
            "Scripts"  => new ScriptsView  { DataContext = Vm.Scripts  },
            "Query"    => new QueryView    { DataContext = Vm.Query    },
            "Watch"    => new WatchView    { DataContext = Vm.Watch    },
            "Settings" => new SettingsView { DataContext = Vm.Settings },
            _          => new TextBlock { Text = "?" }
        };
        _host.Content = view;
    }
}
