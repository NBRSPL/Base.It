using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Base.It.App.ViewModels;

namespace Base.It.App.Views;

public partial class SyncView : UserControl, ISupportsFind
{
    private SyncViewModel? _hookedVm;

    public SyncView()
    {
        InitializeComponent();
        WireSourceFilter();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => UnhookVm();
    }

    /// <summary>Focus → open the dropdown immediately. Same pattern as BatchView.</summary>
    private void OnEndpointPickerGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is AutoCompleteBox box) box.IsDropDownOpen = true;
    }

    /// <summary>Source chevron — focus + open the dropdown.</summary>
    private void OnSourceChevronClick(object? sender, RoutedEventArgs e)
    {
        var box = this.FindControl<AutoCompleteBox>("SourceBox");
        if (box is null) return;
        box.Focus();
        box.IsDropDownOpen = true;
    }

    /// <summary>Target chevron — focus + open the dropdown.</summary>
    private void OnTargetChevronClick(object? sender, RoutedEventArgs e)
    {
        var box = this.FindControl<AutoCompleteBox>("TargetAddBox");
        if (box is null) return;
        box.Focus();
        box.IsDropDownOpen = true;
    }

    /// <summary>
    /// After a target is picked, clear the typed search text so the next
    /// "Add target" starts with a blank field. Deferred via Dispatcher.Post
    /// so we don't fight the AutoCompleteBox's own selection-change pipeline.
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

    /// <summary>× on a chip — untick the matching <see cref="TargetPickVm"/>.</summary>
    private void OnRemoveTargetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not TargetPickVm t) return;
        if (DataContext is not SyncViewModel vm) return;
        e.Handled = true;
        vm.UncheckTarget(t);
    }

    private void WireSourceFilter()
    {
        // Same filter shape on both pickers — consistent UX with Batch.
        var src = this.FindControl<AutoCompleteBox>("SourceBox");
        var tgt = this.FindControl<AutoCompleteBox>("TargetAddBox");
        if (src is not null) src.ItemFilter = EndpointFilter;
        if (tgt is not null) tgt.ItemFilter = EndpointFilter;
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

    /// <summary>ISupportsFind: maps the global find overlay to the Sync target filter.</summary>
    public void ApplyFind(string? text)
    {
        if (DataContext is not SyncViewModel vm) return;
        vm.TargetFilter = text ?? string.Empty;
    }

    public string CurrentFindText
        => (DataContext as SyncViewModel)?.TargetFilter ?? string.Empty;

    /// <summary>
    /// Subscribe to the VM's preview request so the view can own the Window
    /// instance — keeps Window/UI deps out of the VM. Re-subscribed when the
    /// DataContext changes (theme reload, navigation churn).
    /// </summary>
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        UnhookVm();
        if (DataContext is SyncViewModel vm)
        {
            _hookedVm = vm;
            vm.PreviewRequested += OnPreviewRequested;
        }
    }

    private void UnhookVm()
    {
        if (_hookedVm is null) return;
        _hookedVm.PreviewRequested -= OnPreviewRequested;
        _hookedVm = null;
    }

    private void OnPreviewRequested(BatchPreviewViewModel preview)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var win = new BatchPreviewWindow { DataContext = preview };
        if (owner is not null) win.ShowDialog(owner);
        else                   win.Show();
    }
}
