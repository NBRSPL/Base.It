using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Base.It.App.Views;

/// <summary>
/// Standalone Batch window — used when the user wants to keep the main
/// Batch tab's current state untouched and still send a fresh list to
/// Batch (e.g. from Watch's "Send Changes to Batch"). The DataContext
/// is a separate <see cref="ViewModels.BatchViewModel"/> instance per
/// window so the two states never bleed into each other.
/// </summary>
public partial class BatchWindow : Window
{
    public BatchWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += (_, _) => Services.WindowSizing.ClampToWorkingArea(this);
    }
}
