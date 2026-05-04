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
    public ScriptsView()
    {
        InitializeComponent();
        // Drag-drop on the items card. AddHandler with Tunnel + Bubble
        // so DragOver fires regardless of which child is under the
        // cursor; the empty-state overlay sets IsHitTestVisible=False
        // so it doesn't block the drop.
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent,     OnDrop);
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
