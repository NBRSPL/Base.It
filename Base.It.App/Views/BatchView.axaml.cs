using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Base.It.App.ViewModels;

namespace Base.It.App.Views;

public partial class BatchView : UserControl
{
    public BatchView()
    {
        InitializeComponent();
        WireSourceFilter();
        // Ctrl+F (anywhere on the pane) focuses the name find-box.
        // KeyBinding can't focus a control on its own — handler in code.
        KeyDown += OnPaneKeyDown;
    }

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

    /// <summary>Pane-wide shortcuts. Ctrl+F focuses the items-grid name find-box.</summary>
    private void OnPaneKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var box = this.FindControl<TextBox>("NameFilterBox");
            box?.Focus();
            box?.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>
    /// QuickAdd box (the editable first row above the items grid). Enter
    /// commits the typed text via <see cref="BatchViewModel.PasteText"/>
    /// — single-line input becomes one row, multi-line becomes one row
    /// per non-blank line. Box clears after a successful add.
    /// </summary>
    private void OnQuickAddKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is not TextBox box) return;
        if (DataContext is not BatchViewModel vm) return;
        var text = box.Text ?? "";
        if (string.IsNullOrWhiteSpace(text)) return;
        vm.PasteText(text);
        box.Text = "";
        e.Handled = true;
    }

    /// <summary>
    /// Pasting into the QuickAdd box always fans out into the list —
    /// we read the clipboard ourselves, push it through PasteText, and
    /// suppress the default paste so the box stays empty. This is what
    /// makes pasting an Excel column "just work" without needing the
    /// user to press Enter afterwards.
    /// </summary>
    private async void OnQuickAddPaste(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BatchViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is not { } cb) return;

        e.Handled = true;
        var text = await cb.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text)) return;
        vm.PasteText(text);
        if (sender is TextBox box) box.Text = "";
    }

    /// <summary>
    /// Excel-like keys on the items grid:
    ///   Ctrl+V  → paste newline-separated names from the clipboard.
    ///   Delete  → remove every selected row.
    /// Ctrl+C is built into Avalonia's DataGrid via ClipboardCopyMode,
    /// so we don't intercept it. Drag/Shift/Ctrl multi-select is the
    /// standard Extended-mode behaviour.
    /// </summary>
    private async void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not BatchViewModel vm) return;
        if (sender is not DataGrid grid) return;

        // Ctrl+V — paste a list. Read the clipboard via the active TopLevel
        // (Avalonia 11 doesn't expose a static Clipboard helper).
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is { } cb)
            {
                var text = await cb.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    vm.PasteText(text);
                    e.Handled = true;
                }
            }
            return;
        }

        // Delete — remove every selected row. Snapshot the selection
        // before mutating Items so the SelectedItems collection doesn't
        // shift under us mid-iteration.
        if (e.Key == Key.Delete)
        {
            var selected = grid.SelectedItems.OfType<BatchItem>().ToList();
            if (selected.Count > 0)
            {
                vm.DeleteRows(selected);
                e.Handled = true;
            }
            return;
        }
    }
}
