using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Base.It.App.Services;
using Base.It.App.ViewModels;

namespace Base.It.App.Views;

/// <summary>
/// Code-behind for the Snapshots screen. Two responsibilities:
/// <list type="bullet">
///   <item>Inline rename interaction on snapshot list rows — pencil
///         click swaps to TextBox; Enter / blur commit, Escape cancels.</item>
///   <item>Auto-scroll the page to the diff result the moment a
///         Compare run lands (otherwise the user has to scroll down
///         manually every time).</item>
/// </list>
/// The "select all" / sort / filter affordances are all bound in XAML
/// to commands on the VM — no header-injection plumbing lives here.
///
/// Ctrl+F: routes through <see cref="ISupportsFind.FocusFindBox"/>
/// to focus the page's visible filter textbox (EntryFilterBox when a
/// snapshot is selected, DiffFilterBox when the compare result is
/// visible). The filter textbox is the only "search" UI on the page;
/// Ctrl+F is just a keyboard shortcut for it.
/// </summary>
public partial class SnapshotsView : UserControl, ISupportsFind
{
    private SnapshotsViewModel? _hookedVm;

    /// <summary>Export the snapshot's object list (current filter + sort) to CSV.</summary>
    private async void OnExportEntriesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SnapshotsViewModel vm) return;
        await Services.CsvExport.SaveAsync(this, "snapshot-objects.csv",
            vm.EntriesCsvHeaders, vm.EntriesCsvRows(), vm.EntriesHaveRows, vm.Toasts);
    }

    /// <summary>Export the cross-store diff rows (current filter + sort) to CSV.</summary>
    private async void OnExportDiffClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SnapshotsViewModel vm) return;
        await Services.CsvExport.SaveAsync(this, "snapshot-diff.csv",
            vm.DiffCsvHeaders, vm.DiffCsvRows(), vm.DiffHasRows, vm.Toasts);
    }

    public SnapshotsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => UnhookVm();
    }

    /// <summary>
    /// Put keyboard focus on whichever filter textbox is currently
    /// useful on this page: the Entries filter when a snapshot is open
    /// (the user is most likely browsing objects), otherwise the
    /// compare-grid filter when a diff result is showing. If neither
    /// is visible, fall through with <c>false</c> so the caller knows
    /// Ctrl+F did nothing here.
    /// </summary>
    public bool FocusFindBox()
    {
        if (DataContext is SnapshotsViewModel vm)
        {
            if (vm.SelectedSnapshot is not null)
            {
                var box = this.FindControl<TextBox>("EntryFilterBox");
                if (box is not null && box.IsVisible)
                {
                    box.Focus();
                    box.SelectAll();
                    return true;
                }
            }
            if (vm.DiffHasResult)
            {
                var box = this.FindControl<TextBox>("DiffFilterBox");
                if (box is not null && box.IsVisible)
                {
                    box.Focus();
                    box.SelectAll();
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Subscribe to the VM's <see cref="SnapshotsViewModel.DiffResultReady"/>
    /// event so we can auto-scroll the page to the diff result the moment
    /// a Compare run lands.
    /// </summary>
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        UnhookVm();
        if (DataContext is SnapshotsViewModel vm)
        {
            _hookedVm = vm;
            vm.DiffResultReady += OnDiffResultReady;
        }
    }

    private void UnhookVm()
    {
        if (_hookedVm is null) return;
        _hookedVm.DiffResultReady -= OnDiffResultReady;
        _hookedVm = null;
    }

    private void OnDiffResultReady()
    {
        // Defer to the dispatcher so the diff result grid has time to
        // render and update its measured size before we scroll past it.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var scroll = this.FindControl<ScrollViewer>("PageScroll");
            scroll?.ScrollToEnd();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Pencil-icon click on a snapshot row. Enters edit mode and
    /// focuses the TextBox once it becomes visible (Dispatcher.Post
    /// gives the IsEditing binding time to flip the controls).
    /// </summary>
    private void OnSnapshotNameEditClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not SnapshotSummaryVm row) return;
        if (DataContext is not SnapshotsViewModel vm) return;
        e.Handled = true;

        vm.BeginRenameSnapshot(row);

        Dispatcher.UIThread.Post(() =>
        {
            if (btn.Parent is not Grid grid) return;
            foreach (var child in grid.GetVisualDescendants())
            {
                if (child is TextBox tb && ReferenceEquals(tb.Tag, row))
                {
                    tb.Focus();
                    tb.SelectAll();
                    return;
                }
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>Enter commits; Escape cancels.</summary>
    private async void OnSnapshotNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.Tag is not SnapshotSummaryVm row) return;
        if (DataContext is not SnapshotsViewModel vm) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await vm.CommitRenameSnapshotAsync(row);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CancelRenameSnapshot(row);
        }
    }

    /// <summary>
    /// Click-outside / Tab-away commits the pending rename — saving
    /// is the friendlier default than discarding typed input.
    /// </summary>
    private async void OnSnapshotNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.Tag is not SnapshotSummaryVm row) return;
        if (DataContext is not SnapshotsViewModel vm) return;
        if (!row.IsEditing) return;
        await vm.CommitRenameSnapshotAsync(row);
    }

    /// <summary>
    /// Ctrl+C on the Snapshot Entries grid → copy the FullName of each
    /// highlighted row to the clipboard, one per line. No checkbox column
    /// here (entries are a flat list), so the "ticked rows win" rule that
    /// covers Recent Changes / Diff doesn't apply — pass null for the
    /// ticked set and the helper falls back to grid.SelectedItems.
    /// </summary>
    private async void OnEntriesGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (sender is not DataGrid grid) return;
        if (DataContext is not SnapshotsViewModel vm) return;

        var highlighted = grid.SelectedItems.OfType<SnapshotEntryVm>().ToList();
        var copied = await GridCopyHelper.CopyFullNamesAsync<SnapshotEntryVm>(
            top:              TopLevel.GetTopLevel(this),
            tickedItems:      null,
            highlightedItems: highlighted,
            getFullName:      r => r.FullName);
        if (copied > 0)
        {
            e.Handled = true;
            vm.NotifyCopied(copied);
        }
    }

    /// <summary>
    /// Ctrl+C on the cross-store Compare diff grid → copy FullNames.
    /// Same rule as Recent Changes: ticked rows (the user's promote
    /// list) win over highlighted rows.
    /// </summary>
    private async void OnDiffGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (sender is not DataGrid grid) return;
        if (DataContext is not SnapshotsViewModel vm) return;

        var ticked      = vm.DiffRows.Where(r => r.IsSelected).ToList();
        var highlighted = grid.SelectedItems.OfType<SnapshotDiffRowVm>().ToList();
        var copied = await GridCopyHelper.CopyFullNamesAsync<SnapshotDiffRowVm>(
            top:              TopLevel.GetTopLevel(this),
            tickedItems:      ticked,
            highlightedItems: highlighted,
            getFullName:      r => r.FullName);
        if (copied > 0)
        {
            e.Handled = true;
            vm.NotifyCopied(copied);
        }
    }

    /// <summary>
    /// Compare-grid eye button: opens the side-by-side preview window
    /// for one diff row. The VM does the snapshot-store reads and
    /// builds a <see cref="BatchPreviewViewModel"/> populated with two
    /// pre-aligned panes (FROM / TO), so we just open the existing
    /// preview window — no fetch happens here.
    /// </summary>
    private async void OnDiffRowPreviewClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not SnapshotDiffRowVm row) return;
        if (DataContext is not SnapshotsViewModel vm) return;
        e.Handled = true;

        var preview = await vm.BuildDiffPreviewAsync(row);
        if (preview is null) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var win = new BatchPreviewWindow { DataContext = preview };
        // Non-modal — multiple diff previews can be open in parallel
        // without freezing the snapshots page underneath.
        if (owner is not null) win.Show(owner);
        else                   win.Show();
    }
}
