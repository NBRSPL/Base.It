using Avalonia.Controls;
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
