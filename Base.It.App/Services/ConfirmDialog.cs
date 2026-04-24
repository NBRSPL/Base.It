using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace Base.It.App.Services;

/// <summary>
/// Tiny wrapper around FluentAvalonia's <see cref="ContentDialog"/> for the
/// "are you sure?" pattern. Returns true when the user confirms, false on
/// cancel / dismiss. Cancel is the default focused button so mashing Enter
/// doesn't trigger a destructive delete.
/// </summary>
public static class ConfirmDialog
{
    public static async Task<bool> AskAsync(
        string title,
        string message,
        string primaryText = "Delete",
        string cancelText  = "Cancel")
    {
        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };

        var dlg = new ContentDialog
        {
            Title              = title,
            Content            = body,
            PrimaryButtonText  = primaryText,
            CloseButtonText    = cancelText,
            DefaultButton      = ContentDialogButton.Close,
        };

        // FluentAvalonia's ContentDialog hosts itself inside the active
        // top-level (AppWindow). No explicit owner needed.
        var result = await dlg.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
