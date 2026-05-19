using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Base.It.App.ViewModels;

namespace Base.It.App.Views;

/// <summary>
/// Code-behind for the Scripts pane. Owns the file/folder pickers and
/// the drag-drop wiring — the VM doesn't know about Avalonia's
/// StorageProvider, so the View is the right place to talk to it.
/// </summary>
public partial class ScriptsView : UserControl
{
    private ScriptsViewModel? _hookedVm;

    public ScriptsView()
    {
        InitializeComponent();
        // Drag-drop on the items card. AddHandler with Tunnel + Bubble
        // so DragOver fires regardless of which child is under the
        // cursor; the empty-state overlay sets IsHitTestVisible=False
        // so it doesn't block the drop.
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent,     OnDrop);
        WireTargetFilter();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => UnhookVm();
    }

    /// <summary>Focus → open the dropdown. Same pattern as BatchView/SyncView.</summary>
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
        if (DataContext is not ScriptsViewModel vm) return;
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

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnhookVm();
        if (DataContext is ScriptsViewModel vm)
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
    /// "View" button on a failed row → open the full error in its own
    /// window with a Copy action. Same affordance Batch uses, so failed
    /// scripts surface their actual SQL error rather than a clipped cell.
    /// </summary>
    private void OnViewErrorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not ScriptItem item) return;
        e.Handled = true;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var win = new ErrorDetailWindow();
        win.Show(
            title:    item.FileName,
            subtitle: $"Failed — row #{item.Index}",
            body:     item.Message);
        // Non-modal — see BatchView.OnViewErrorClick for the rationale.
        if (owner is not null) win.Show(owner);
        else                   win.Show();
    }

    /// <summary>
    /// Eye icon → ask the VM to build a preview (file content + per-target
    /// fetches when the script's object can be detected), then open the
    /// shared diff window.
    /// </summary>
    private void OnPreviewClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not ScriptItem item) return;
        if (DataContext is not ScriptsViewModel vm) return;
        e.Handled = true;
        vm.RequestPreview(item);
    }

    private void OnPreviewRequested(BatchPreviewViewModel preview)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var win = new BatchPreviewWindow { DataContext = preview };
        // Non-modal — see BatchView.OnPreviewClick for the rationale.
        if (owner is not null) win.Show(owner);
        else                   win.Show();
    }

    /// <summary>
    /// Right-click → Open in Explorer. Launches the OS file browser at the
    /// file's parent folder with the file pre-selected (Windows-only
    /// flag; falls back to opening the folder on other platforms).
    /// </summary>
    private void OnOpenInExplorer(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not ScriptItem item) return;
        if (string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath)) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = "explorer.exe",
                    Arguments       = $"/select,\"{item.FilePath}\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = Path.GetDirectoryName(item.FilePath) ?? item.FilePath,
                    UseShellExecute = true,
                });
            }
        }
        catch { /* best-effort — explorer failures aren't worth a toast */ }
    }

    /// <summary>Right-click → Copy location. Puts the absolute path on the clipboard.</summary>
    private async void OnCopyLocation(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not ScriptItem item) return;
        if (string.IsNullOrWhiteSpace(item.FilePath)) return;
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is null) return;
        await top.Clipboard.SetTextAsync(item.FilePath);
    }

    /// <summary>Right-click → Open file. Launches the OS default app for .sql (usually SSMS or a text editor).</summary>
    private void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not ScriptItem item) return;
        if (string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = item.FilePath,
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ScriptsViewModel vm) return;
        if (!e.Data.Contains(DataFormats.Files)) return;

        var files = e.Data.GetFiles();
        if (files is null) return;

        var paths = new List<string>();
        foreach (var f in files)
        {
            var local = f.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(local)) paths.Add(local!);
        }
        if (paths.Count > 0) vm.AddPaths(paths);
        e.Handled = true;
    }

    private async void OnAddFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScriptsViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Pick .sql script(s) to execute",
            AllowMultiple  = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SQL scripts") { Patterns = new[] { "*.sql" } },
                FilePickerFileTypes.All
            }
        });
        if (files is null || files.Count == 0) return;

        var paths = files.Select(f => f.TryGetLocalPath())
                         .Where(p => !string.IsNullOrWhiteSpace(p))
                         .Cast<string>()
                         .ToList();
        if (paths.Count > 0) vm.AddPaths(paths);
    }

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScriptsViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Pick a folder — every .sql below it will be added",
            AllowMultiple = false,
        });
        var folder = picked?.FirstOrDefault();
        if (folder is null) return;
        var path = folder.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        vm.AddPaths(new[] { path! });
    }
}
