using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Base.It.App.Services;

namespace Base.It.App.Views;

public partial class ToastHost : UserControl
{
    public ToastHost()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDismiss(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not ToastItem item) return;
        if (DataContext is not ToastService svc) return;
        svc.Dismiss(item);
    }

    /// <summary>
    /// Action button on a sticky toast (e.g. "Update now"). Fires the
    /// toast's Action callback first, then removes the toast — the action
    /// usually navigates somewhere or kicks off a background task, and we
    /// want the prompt to disappear so the user knows the click registered.
    /// </summary>
    private void OnAction(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not ToastItem item) return;
        if (DataContext is not ToastService svc) return;
        try { item.Action?.Invoke(); }
        finally { svc.Dismiss(item); }
    }
}
