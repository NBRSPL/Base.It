using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Base.It.App.Services;
using Base.It.App.ViewModels;

namespace Base.It.App.Views;

// Intentionally does NOT implement ISupportsFind: Ctrl+F used to set
// the items-grid name filter, which surprised users who expected the
// OS-standard "find in page" behaviour. The grid filter has its own
// visible textbox; Ctrl+F stays separate.
public partial class BatchView : UserControl
{
    public BatchView()
    {
        InitializeComponent();
        WireSourceFilter();
    }

    /// <summary>Export the currently-visible batch rows (after filter + sort) to CSV.</summary>
    private async void OnExportCsvClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BatchViewModel vm) return;
        await Services.CsvExport.SaveAsync(this, vm, vm.Toasts);
    }

    /// <summary>
    /// Focus → open the dropdown immediately. AutoCompleteBox normally
    /// shows the popup only after the user types, which makes the control
    /// feel like a textbox rather than a dropdown. Pairing this with
    /// <c>MinimumPrefixLength=0</c> turns it into a proper "click to see
    /// all options" picker — no chevron click required, though we
    /// provide one too.
    /// </summary>
    private void OnEndpointPickerGotFocus(object? sender, Avalonia.Input.GotFocusEventArgs e)
    {
        if (sender is AutoCompleteBox box) box.IsDropDownOpen = true;
    }

    /// <summary>Chevron next to the source picker — focus + open the dropdown so the user sees every available source.</summary>
    private void OnSourceChevronClick(object? sender, RoutedEventArgs e)
    {
        var box = this.FindControl<AutoCompleteBox>("SourceBox");
        if (box is null) return;
        box.Focus();
        box.IsDropDownOpen = true;
    }

    /// <summary>Chevron next to the target picker — focus + open the dropdown so every still-available target is listed.</summary>
    private void OnTargetChevronClick(object? sender, RoutedEventArgs e)
    {
        var box = this.FindControl<AutoCompleteBox>("TargetAddBox");
        if (box is null) return;
        box.Focus();
        box.IsDropDownOpen = true;
    }

    /// <summary>
    /// After a target is picked, clear the typed search text so the next
    /// "Add target" starts with a blank field. The VM's
    /// OnNextTargetEndpointChanged resets SelectedItem to null already;
    /// without also wiping Text the picker would show the just-picked
    /// item's label as residue when the user re-opens the dropdown.
    /// Deferred via Dispatcher.Post so we don't fight the AutoCompleteBox's
    /// own selection-change pipeline.
    /// </summary>
    private void OnTargetAddSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not AutoCompleteBox box) return;
        if (box.SelectedItem is null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            box.Text = string.Empty;
            box.SelectedItem = null;
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void WireSourceFilter()
    {
        // Source side now lists BatchSourceItem (live + snapshot mixed);
        // target side still lists plain EndpointPick.
        var src = this.FindControl<AutoCompleteBox>("SourceBox");
        var tgt = this.FindControl<AutoCompleteBox>("TargetAddBox");
        if (src is not null) src.ItemFilter = SourceItemFilter;
        if (tgt is not null) tgt.ItemFilter = EndpointFilter;
    }

    /// <summary>
    /// Filter for the source picker. Matches the combined label (which
    /// includes "@ snapshot …" for snapshot rows) plus the underlying
    /// env / db so users can find either kind by typing any fragment.
    /// </summary>
    private static bool SourceItemFilter(string? search, object? item)
    {
        if (item is not BatchSourceItem s) return false;
        if (string.IsNullOrEmpty(search)) return true;
        var q = search!.Trim();
        return s.Label.Contains(q, System.StringComparison.OrdinalIgnoreCase)
            || s.SubLabel.Contains(q, System.StringComparison.OrdinalIgnoreCase)
            || s.Endpoint.Environment.Contains(q, System.StringComparison.OrdinalIgnoreCase)
            || s.Endpoint.Database.Contains(q, System.StringComparison.OrdinalIgnoreCase)
            || (s.IsSnapshot && "snapshot".Contains(q, System.StringComparison.OrdinalIgnoreCase));
    }

    private static bool EndpointFilter(string? search, object? item)
    {
        if (item is not EndpointPick p) return false;
        if (string.IsNullOrEmpty(search)) return true;
        var s = search!.Trim();
        return p.Label.Contains(s, System.StringComparison.OrdinalIgnoreCase)
            || p.Environment.Contains(s, System.StringComparison.OrdinalIgnoreCase)
            || p.Database.Contains(s, System.StringComparison.OrdinalIgnoreCase);
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
    /// Load button — open a file picker for CSV/XLSX. Replaces the old
    /// "type a path then press Load" flow: power users can still paste a
    /// path into the textbox next to it; everyone else gets the OS picker.
    /// </summary>
    private async void OnLoadClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BatchViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        // If the user already typed a path, honour it — same shortcut as
        // hitting Enter on the textbox. Picker only opens when there's
        // nothing to load.
        if (!string.IsNullOrWhiteSpace(vm.FilePath) && System.IO.File.Exists(vm.FilePath))
        {
            vm.LoadFromFileCommand.Execute(null);
            return;
        }

        var picks = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Pick an object list (CSV or XLSX)",
            AllowMultiple  = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Object lists") { Patterns = new[] { "*.csv", "*.xlsx" } },
                new FilePickerFileType("CSV")          { Patterns = new[] { "*.csv" } },
                new FilePickerFileType("XLSX")         { Patterns = new[] { "*.xlsx" } },
                FilePickerFileTypes.All
            }
        });
        var f = picks?.FirstOrDefault();
        if (f is null) return;

        var local = f.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(local)) return;
        vm.FilePath = local!;
        vm.LoadFromFileCommand.Execute(null);
    }

    /// <summary>
    /// × button on a selected-target chip — unticks the underlying
    /// <see cref="TargetPickVm"/> so the chip disappears (via
    /// CheckedTargets) and the count updates. Mirrors the "click the
    /// ToggleButton chip to deselect" workflow that used to live in the
    /// popover, but inline so the user doesn't have to open a flyout.
    /// </summary>
    private void OnRemoveTargetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not TargetPickVm t) return;
        if (DataContext is not BatchViewModel vm) return;
        e.Handled = true;
        vm.UncheckTarget(t);
    }

    /// <summary>
    /// "View" button on a failed row → open the full-error window with a
    /// Copy-to-clipboard action. The Message cell only has space for a
    /// one-liner; real SQL errors are often a full paragraph and the user
    /// needs the complete text for a ticket / search.
    /// </summary>
    private void OnViewErrorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not BatchItem item) return;
        e.Handled = true;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var win = new ErrorDetailWindow();
        win.Show(
            title:    item.Name,
            subtitle: $"Failed — row #{item.Index}",
            body:     item.Message);
        // Non-modal so it doesn't freeze the Batch window — the user
        // typically wants to copy the error AND keep working on other
        // rows. Show(owner) keeps the always-on-top-of-owner relationship
        // without the modal block.
        if (owner is not null) win.Show(owner);
        else                   win.Show();
    }

    /// <summary>
    /// Eye icon → open the preview window for this row. Fetches source +
    /// every ticked target's CREATE definition lazily, one tab per
    /// endpoint. Pure read; nothing is executed against any target.
    /// </summary>
    private async void OnPreviewClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not BatchItem item) return;
        if (DataContext is not BatchViewModel vm) return;
        e.Handled = true;

        // Async now: snapshot-source mode reads the literal SQL from the
        // schema store before opening the window — without that, a
        // snapshot source would try a (doomed) live fetch and the preview
        // would surface "not found in the endpoint".
        var preview = await vm.BuildPreviewAsync(item);
        if (preview is null) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var win = new BatchPreviewWindow { DataContext = preview };
        // Non-modal so multiple previews can be open at once across
        // multiple Batch windows. Show(owner) preserves the
        // owner relationship (preview floats with its parent, follows it
        // on minimize/restore) WITHOUT blocking the parent — minimizing
        // or even leaving the preview open no longer freezes the Batch
        // window underneath it.
        if (owner is not null) win.Show(owner);
        else                   win.Show();
    }

    /// <summary>
    /// Excel-like keys on the items grid:
    ///   Ctrl+C  → copy the Name of each selected row (ticked rows win
    ///             over highlighted rows), one per line. Replaces the
    ///             built-in DataGrid copy (which produced TSV-shaped cell
    ///             dumps unsuited to pasting back into Batch). The grid
    ///             has ClipboardCopyMode="None" in XAML so the built-in
    ///             handler doesn't fight this one.
    ///   Ctrl+V  → paste newline-separated names from the clipboard.
    ///   Delete  → remove every selected row.
    /// Drag/Shift/Ctrl multi-select is the standard Extended-mode behaviour.
    /// </summary>
    private async void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not BatchViewModel vm) return;
        if (sender is not DataGrid grid) return;

        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Ticked rows are the user's curated set (the same set
            // Execute Selected / Remove Selected act on); fall back to
            // highlight selection if nothing's ticked.
            var ticked      = vm.Items.Where(i => i.IsSelected).ToList();
            var highlighted = grid.SelectedItems.OfType<BatchItem>().ToList();
            var copied = await GridCopyHelper.CopyFullNamesAsync<BatchItem>(
                top:              TopLevel.GetTopLevel(this),
                tickedItems:      ticked,
                highlightedItems: highlighted,
                getFullName:      i => i.Name);
            if (copied > 0)
            {
                e.Handled = true;
                vm.NotifyCopied(copied);
            }
            return;
        }

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
