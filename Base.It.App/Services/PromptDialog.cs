using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace Base.It.App.Services;

/// <summary>
/// Single-line text-input dialog backed by FluentAvalonia's ContentDialog.
/// Returns the trimmed input on confirm, or null on cancel / blank submit.
/// Cancel is the default focused button — Enter on the input also confirms.
/// </summary>
public static class PromptDialog
{
    public static async Task<string?> AskAsync(
        string title,
        string message,
        string initialValue = "",
        string watermark    = "",
        string primaryText  = "Save",
        string cancelText   = "Cancel")
    {
        var info = new TextBlock
        {
            Text         = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth     = 420,
            Margin       = new Avalonia.Thickness(0, 0, 0, 8),
        };

        var box = new TextBox
        {
            Text      = initialValue,
            Watermark = watermark,
            MinWidth  = 320,
        };

        var body = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children    = { info, box },
        };

        var dlg = new ContentDialog
        {
            Title             = title,
            Content           = body,
            PrimaryButtonText = primaryText,
            CloseButtonText   = cancelText,
            DefaultButton     = ContentDialogButton.Primary,
        };

        // Auto-select content so Enter confirms with whatever the user typed
        // and the initial value is easy to overwrite.
        box.AttachedToVisualTree += (_, _) =>
        {
            box.SelectAll();
            box.Focus();
        };

        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;

        var value = (box.Text ?? "").Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
