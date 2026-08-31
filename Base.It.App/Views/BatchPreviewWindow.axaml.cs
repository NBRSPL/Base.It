using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Base.It.App.ViewModels;

namespace Base.It.App.Views;

/// <summary>
/// Standalone preview window. Owns the window chrome (Title, Close,
/// Ctrl+F find overlay) but delegates the actual pane rendering to
/// <see cref="PaneDiffView"/>, which is also embedded inline on the
/// merged Sync screen. One renderer, two entry points.
/// </summary>
public partial class BatchPreviewWindow : Window
{
    private BatchPreviewViewModel? _vm;

    public BatchPreviewWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Bind();
        Opened += (_, _) => Services.WindowSizing.ClampToWorkingArea(this);
        Opened += async (_, _) => { if (_vm is not null) await _vm.LoadAsync(); };
        Closed += (_, _) => Unbind();

        // Ctrl+F opens the standard find bar (AvaloniaEdit's SearchPanel:
        // find next / previous / highlight-all + match count) on the focused
        // side. F3 / Shift+F3 for find-next/prev are handled by that panel
        // itself once it's open. Change navigation is on the ▲ / ▼ buttons.
        AddHandler(KeyDownEvent, OnGlobalKeyDown,
            Avalonia.Interactivity.RoutingStrategies.Bubble | Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var host = this.FindControl<PaneDiffView>("PaneHost");
            host?.OpenFind();
            e.Handled = true;
        }
    }

    private void Bind()
    {
        Unbind();
        _vm = DataContext as BatchPreviewViewModel;
        if (_vm is not null) _vm.ScrollToLineRequested += OnScrollToLineRequested;
        // PaneDiffView binds to the same DataContext via XAML; no
        // additional wiring needed there.
    }

    private void Unbind()
    {
        if (_vm is null) return;
        _vm.ScrollToLineRequested -= OnScrollToLineRequested;
        _vm = null;
    }

    /// <summary>
    /// VM raised <c>ScrollToLineRequested(lineIndex)</c> from a Next/Prev
    /// change click. Hand it to the embedded PaneDiffView which knows
    /// how to drive every pane's ScrollViewer in lockstep.
    /// </summary>
    private void OnScrollToLineRequested(int lineIndex)
    {
        var host = this.FindControl<PaneDiffView>("PaneHost");
        host?.ScrollToLine(lineIndex);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
