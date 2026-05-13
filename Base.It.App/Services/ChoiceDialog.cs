using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace Base.It.App.Services;

/// <summary>
/// Three-way confirm dialog used when the cancellation default isn't
/// enough — e.g. "Send to Batch" needs Replace / Open-in-new-window /
/// Cancel rather than just yes/no. Returns one of
/// <see cref="ChoiceDialogResult"/>; <see cref="ChoiceDialogResult.Cancel"/>
/// is the default focused button so mashing Enter doesn't pick a
/// destructive Replace by accident.
/// </summary>
public enum ChoiceDialogResult { Primary, Secondary, Cancel }

public static class ChoiceDialog
{
    public static async Task<ChoiceDialogResult> AskAsync(
        string title,
        string message,
        string primaryText,
        string secondaryText,
        string cancelText = "Cancel")
    {
        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
        };

        var dlg = new ContentDialog
        {
            Title                = title,
            Content              = body,
            PrimaryButtonText    = primaryText,
            SecondaryButtonText  = secondaryText,
            CloseButtonText      = cancelText,
            DefaultButton        = ContentDialogButton.Close,
        };

        var result = await dlg.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary   => ChoiceDialogResult.Primary,
            ContentDialogResult.Secondary => ChoiceDialogResult.Secondary,
            _                              => ChoiceDialogResult.Cancel,
        };
    }
}
