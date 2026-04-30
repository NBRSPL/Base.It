using Avalonia.Controls;
using Avalonia.Input;
using Base.It.App.ViewModels;

namespace Base.It.App.Views;

public partial class SyncView : UserControl
{
    public SyncView()
    {
        InitializeComponent();
        WireSourceFilter();
        // Pane-wide Ctrl+F focuses the target filter input.
        KeyDown += OnPaneKeyDown;
    }

    private void OnPaneKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var box = this.FindControl<TextBox>("TargetFilterBox");
            box?.Focus();
            box?.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>
    /// AutoCompleteBox's default filter only matches against ToString() —
    /// when DisplayName is set the env/db pair becomes invisible to
    /// type-ahead. This filter makes the picker searchable by display
    /// name, environment, or database name simultaneously.
    /// </summary>
    private void WireSourceFilter()
    {
        var box = this.FindControl<AutoCompleteBox>("SourceBox");
        if (box is null) return;
        box.ItemFilter = (search, item) =>
        {
            if (item is not EndpointPick p) return false;
            if (string.IsNullOrEmpty(search)) return true;
            var s = search.Trim();
            return p.Label.Contains(s, System.StringComparison.OrdinalIgnoreCase)
                || p.Environment.Contains(s, System.StringComparison.OrdinalIgnoreCase)
                || p.Database.Contains(s, System.StringComparison.OrdinalIgnoreCase);
        };
    }
}
