using Avalonia;
using Avalonia.Controls;
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

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _nav  = this.FindControl<NavigationView>("Nav")!;
        _host = this.FindControl<Frame>("Host")!;
        _groupSelector = this.FindControl<Border>("GroupSelector")!;
        _themeGlyph = this.FindControl<TextBlock>("ThemeGlyph")!;
        _nav.SelectionChanged += OnNavSelectionChanged;

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

    private void OnNavSelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;

        // Hide the Fetch dock on Settings and Home — nothing to fetch there.
        Vm.FetchDock.IsVisible = tag != "Settings" && tag != "Home";
        // Settings manages groups directly; Home shows its own group summary.
        _groupSelector.IsVisible = tag != "Settings" && tag != "Home";

        switch (tag)
        {
            case "Home":    Vm.Home.Refresh();             break;
            case "Compare": Vm.Compare.ReloadDatabases();  break;
            case "Sync":    Vm.Sync.Reload();              break;
            case "Batch":   Vm.Batch.Reload();             break;
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
            "Query"    => new QueryView    { DataContext = Vm.Query    },
            "Watch"    => new WatchView    { DataContext = Vm.Watch    },
            "Settings" => new SettingsView { DataContext = Vm.Settings },
            _          => new TextBlock { Text = "?" }
        };
        _host.Content = view;
    }
}
