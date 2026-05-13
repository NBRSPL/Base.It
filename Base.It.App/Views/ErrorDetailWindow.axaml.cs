using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Base.It.App.Views;

/// <summary>
/// Lightweight modal that shows a single error message with a Copy-to-
/// clipboard action. Used by the Batch / Scripts grids whenever a row
/// fails — the row's Message cell only has space for a one-liner, and
/// real T-SQL / connection errors are often a paragraph long with the
/// full server response. Keeping this in its own window means the user
/// can leave it open while they re-run the action or copy the text into
/// a ticket.
/// </summary>
public partial class ErrorDetailWindow : Window
{
    public ErrorDetailWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += (_, _) => Services.WindowSizing.ClampToWorkingArea(this);
    }

    /// <summary>
    /// Populate the three text blocks. Kept as a single Show() method so
    /// callers don't have to think about which child control owns each
    /// piece of text — the title is the object name, subtitle is the
    /// short status hint, body is the actual error.
    /// </summary>
    public void Show(string title, string subtitle, string body)
    {
        var t = this.FindControl<TextBlock>("TitleBlock");
        var s = this.FindControl<TextBlock>("SubtitleBlock");
        var b = this.FindControl<SelectableTextBlock>("BodyBlock");
        if (t is not null) t.Text = title;
        if (s is not null) s.Text = subtitle;
        if (b is not null) b.Text = body;
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is null) return;
        var body = this.FindControl<SelectableTextBlock>("BodyBlock");
        var text = body?.Text ?? "";
        try { await top.Clipboard.SetTextAsync(text); } catch { /* clipboard hiccups aren't worth a toast */ }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
