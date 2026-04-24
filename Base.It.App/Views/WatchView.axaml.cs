using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Base.It.App.Views;

public partial class WatchView : UserControl
{
    public WatchView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
