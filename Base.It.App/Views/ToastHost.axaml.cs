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
}
