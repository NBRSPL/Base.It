using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Base.It.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
