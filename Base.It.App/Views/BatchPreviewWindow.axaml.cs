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
    private string _findText = "";

    public BatchPreviewWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Bind();
        Opened += (_, _) => Services.WindowSizing.ClampToWorkingArea(this);
        Opened += async (_, _) => { if (_vm is not null) await _vm.LoadAsync(); };
        Closed += (_, _) => Unbind();

        // Window-wide keystrokes:
        //   Ctrl+F          → open find overlay
        //   F3              → next change
        //   Shift+F3        → previous change
        // Mirrors IDE diff conventions so muscle memory works here.
        // Esc closes the find overlay via OnFindBoxKeyDown.
        AddHandler(KeyDownEvent, OnGlobalKeyDown,
            Avalonia.Interactivity.RoutingStrategies.Bubble | Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>Open the find overlay and seed it with the previous query so re-opening picks up where the user left off.</summary>
    private void OpenFindOverlay()
    {
        var ov  = this.FindControl<Border>("FindOverlay");
        var box = this.FindControl<TextBox>("FindBox");
        if (ov is null || box is null) return;
        ov.IsVisible = true;
        box.Text     = _findText;
        box.Focus();
        if (box.Text is { Length: > 0 } t) box.CaretIndex = t.Length;
    }

    private void HideFindOverlay()
    {
        var ov = this.FindControl<Border>("FindOverlay");
        if (ov is not null) ov.IsVisible = false;
        if (_findText.Length > 0)
        {
            _findText = "";
            ApplyFindToPanes(_findText);
        }
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            OpenFindOverlay();
            e.Handled = true;
            return;
        }
        // F3 family — only fire when the find textbox doesn't have
        // focus so the user can still type 'F3' into a search if they
        // ever needed to. The find overlay's own KeyDown handles Esc.
        if (e.Key == Key.F3 && _vm is not null)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _vm.PrevChangeCommand.Execute(null);
            else                                            _vm.NextChangeCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideFindOverlay();
            e.Handled = true;
        }
    }

    private void OnFindBoxTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var next = tb.Text ?? "";
        if (next == _findText) return;
        _findText = next;
        ApplyFindToPanes(_findText);
    }

    private void OnFindClose(object? sender, RoutedEventArgs e) => HideFindOverlay();

    private void ApplyFindToPanes(string text)
    {
        var host = this.FindControl<PaneDiffView>("PaneHost");
        host?.SetFindText(text);
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
