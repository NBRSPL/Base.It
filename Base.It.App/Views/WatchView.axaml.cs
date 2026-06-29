using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Base.It.App.ViewModels;

namespace Base.It.App.Views;

public partial class WatchView : UserControl
{
    private WatchViewModel? _hookedVm;

    public WatchView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => UnhookVm();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        UnhookVm();
        if (DataContext is WatchViewModel vm)
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

    /// <summary>
    /// Eye icon → ask the VM to build a preview, then open the same diff
    /// window Batch / Sync use. The button's Tag carries the row VM so we
    /// don't have to plumb a per-row command parameter through the
    /// ItemsControl template.
    /// </summary>
    private void OnPreviewClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not DriftRowVm row) return;
        if (DataContext is not WatchViewModel vm) return;
        e.Handled = true;
        vm.RequestPreview(row);
    }

    /// <summary>Export every drift row (all sections, current sort) to CSV.</summary>
    private async void OnExportCsvClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WatchViewModel vm) return;
        await Services.CsvExport.SaveAsync(this, vm, vm.Toasts);
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
