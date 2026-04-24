using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
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
    private void OnGroupNameCommit(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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

    /// <summary>
    /// "+" on a group header — routes to the VM's
    /// <see cref="SettingsViewModel.AddConnectionToGroupCommand"/> with the
    /// group id stashed in the button's Tag.
    /// </summary>
    private void OnAddConnectionToGroup(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not System.Guid id) return;
        if (DataContext is not SettingsViewModel vm) return;
        // Stop the click from also toggling the Expander header.
        e.Handled = true;
        vm.AddConnectionToGroupCommand.Execute(id);
    }

    /// <summary>
    /// "✕" on a group header — routes to the VM's
    /// <see cref="SettingsViewModel.DeleteGroupCommand"/> (which itself
    /// prompts for confirmation) with the group id from the button Tag.
    /// </summary>
    private void OnDeleteGroup(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not System.Guid id) return;
        if (DataContext is not SettingsViewModel vm) return;
        e.Handled = true;
        vm.DeleteGroupCommand.Execute(id);
    }

    /// <summary>
    /// "−" on a connection row — routes to the VM's
    /// <see cref="SettingsViewModel.DeleteConnectionCommand"/> with the row
    /// from the button's DataContext (the row template inherits it).
    /// </summary>
    private void OnDeleteConnection(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not ConnectionRow row) return;
        if (DataContext is not SettingsViewModel vm) return;
        e.Handled = true;
        vm.DeleteConnectionCommand.Execute(row);
    }
}
