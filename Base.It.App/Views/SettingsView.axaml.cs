using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Base.It.App.ViewModels;

namespace Base.It.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>
    /// Clicking anywhere on a group node's Expander syncs the right-panel
    /// editor to that group. Without this the rename textbox and member
    /// list would stay on whatever the ComboBox last selected, which is
    /// the behaviour the user was tripping over.
    /// </summary>
    private void OnGroupNodeTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control c) return;
        if (c.DataContext is not ConnectionGroupNodeVm node) return;
        if (DataContext is not SettingsViewModel vm) return;

        var match = vm.Groups.FirstOrDefault(g => g.Id == node.GroupId);
        if (match is not null && !ReferenceEquals(vm.SelectedGroup, match))
            vm.SelectedGroup = match;
    }

    /// <summary>Commit the inline rename on blur.</summary>
    private void OnGroupNameCommit(object? sender, RoutedEventArgs e)
        => CommitInlineRename(sender);

    /// <summary>Commit on Enter; revert on Escape.</summary>
    private void OnGroupNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitInlineRename(sender);
        }
        else if (e.Key == Key.Escape && sender is TextBox tbRevert &&
                 tbRevert.DataContext is ConnectionGroupNodeVm node &&
                 DataContext is SettingsViewModel vm)
        {
            var original = vm.Groups.FirstOrDefault(g => g.Id == node.GroupId);
            if (original is not null) node.GroupName = original.Name;
            e.Handled = true;
        }
    }

    private void CommitInlineRename(object? sender)
    {
        if (sender is not TextBox tb) return;
        if (tb.DataContext is not ConnectionGroupNodeVm node) return;
        if (DataContext is not SettingsViewModel vm) return;

        _ = vm.RenameGroupFromTreeAsync(node.GroupId, node.GroupName);
    }

    private void OnAddConnectionToGroup(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not Guid id) return;
        if (DataContext is not SettingsViewModel vm) return;
        e.Handled = true;
        vm.AddConnectionToGroupCommand.Execute(id);
    }

    private void OnDeleteGroup(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not Guid id) return;
        if (DataContext is not SettingsViewModel vm) return;
        e.Handled = true;
        vm.DeleteGroupCommand.Execute(id);
    }

    private void OnDeleteConnection(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not ConnectionRow row) return;
        if (DataContext is not SettingsViewModel vm) return;
        e.Handled = true;
        vm.DeleteConnectionCommand.Execute(row);
    }
}
