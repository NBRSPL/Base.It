using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Base.It.App.ViewModels;

namespace Base.It.App.Views;

// Intentionally does NOT implement ISupportsFind: Ctrl+F used to set
// the Sync target filter, which surprised users who expected the
// OS-standard "find in page" behaviour. The target filter has its
// own visible textbox; Ctrl+F stays separate.
public partial class SyncView : UserControl
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

    /// <summary>Source chevron — focus + open the dropdown so every source candidate shows.</summary>
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
        // Source side lists BatchSourceItem (live + snapshot mixed);
        // target side still lists plain EndpointPick. Matches Batch's
        // exact filter wiring so source/target behaviour is identical.
        var src = this.FindControl<AutoCompleteBox>("SourceBox");
        var tgt = this.FindControl<AutoCompleteBox>("TargetAddBox");
        if (src is not null) src.ItemFilter = SourceItemFilter;
        if (tgt is not null) tgt.ItemFilter = EndpointFilter;
    }

    /// <summary>
    /// Filter for the source picker. Matches the combined label (which
    /// includes "@ snapshot …" for snapshot rows) plus the underlying
    /// env / db so users can find either kind by typing any fragment.
    /// Copied verbatim from BatchView so both screens behave identically.
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
        // Non-modal — see BatchView.OnPreviewClick for the rationale.
        if (owner is not null) win.Show(owner);
        else                   win.Show();
    }
}
