using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Base.It.App.ViewModels;

namespace Base.It.App.Views;

/// <summary>
/// Code-behind for the Query pane. Owns the AutoCompleteBox dropdown
/// handlers — same shape Batch / Sync / Scripts use, so the picker
/// feels consistent across screens.
/// </summary>
public partial class QueryView : UserControl
{
    public QueryView()
    {
        InitializeComponent();
        WireTargetFilter();
    }

    /// <summary>Export the last run's result rows to CSV.</summary>
    private async void OnExportCsvClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not QueryViewModel vm) return;
        await Services.CsvExport.SaveAsync(this, vm, vm.Toasts);
    }

    /// <summary>Focus → open the dropdown.</summary>
    private void OnEndpointPickerGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is AutoCompleteBox box) box.IsDropDownOpen = true;
    }

    /// <summary>Chevron — focus + open the dropdown.</summary>
    private void OnTargetChevronClick(object? sender, RoutedEventArgs e)
    {
        var box = this.FindControl<AutoCompleteBox>("TargetAddBox");
        if (box is null) return;
        box.Focus();
        box.IsDropDownOpen = true;
    }

    /// <summary>After a target is picked, clear the typed search text so the next add starts blank.</summary>
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
        if (DataContext is not QueryViewModel vm) return;
        e.Handled = true;
        vm.UncheckTarget(t);
    }

    private void WireTargetFilter()
    {
        var tgt = this.FindControl<AutoCompleteBox>("TargetAddBox");
        if (tgt is not null) tgt.ItemFilter = EndpointFilter;
    }

    private static bool EndpointFilter(string? search, object? item)
    {
        if (item is not EndpointPick p) return false;
        if (string.IsNullOrEmpty(search)) return true;
        var s = search!.Trim();
        return p.Label.Contains(s, StringComparison.OrdinalIgnoreCase)
            || p.Environment.Contains(s, StringComparison.OrdinalIgnoreCase)
            || p.Database.Contains(s, StringComparison.OrdinalIgnoreCase);
    }
}
