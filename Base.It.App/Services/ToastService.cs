using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Base.It.App.Services;

public enum ToastKind { Info, Success, Warning, Error }

/// <summary>
/// One visible toast row. Auto-removed by the service timer; the user can
/// also dismiss it with the X button via <see cref="DismissCommand"/>.
/// </summary>
public sealed partial class ToastItem : ObservableObject
{
    public Guid     Id      { get; } = Guid.NewGuid();
    public ToastKind Kind   { get; init; }
    [ObservableProperty] private string _title   = "";
    [ObservableProperty] private string _message = "";

    /// <summary>
    /// Optional action button shown on the toast. When non-empty, the toast
    /// becomes "sticky" (no auto-dismiss) so the user has time to act on it.
    /// </summary>
    public string ActionLabel { get; init; } = "";
    public Action? Action { get; init; }

    public bool ShowMessage => !string.IsNullOrWhiteSpace(Message);
    public bool ShowAction  => !string.IsNullOrWhiteSpace(ActionLabel) && Action is not null;
    public bool IsSticky    => ShowAction;

    public string KindClass => Kind switch
    {
        ToastKind.Success => "success",
        ToastKind.Warning => "warning",
        ToastKind.Error   => "error",
        _                 => "info",
    };

    public string Glyph => Kind switch
    {
        ToastKind.Success => "✓",
        ToastKind.Warning => "!",
        ToastKind.Error   => "✕",
        _                 => "i",
    };
}

/// <summary>
/// Global pop-out notification system. Any VM with a reference to
/// <see cref="AppServices"/> can fire a toast; the host view
/// (MainWindow's ToastHost) binds to <see cref="Items"/>. Every toast
/// auto-dismisses after 4 seconds unless explicitly removed earlier.
/// Thread-safe: calls from non-UI threads marshal onto the UI thread.
/// </summary>
public sealed class ToastService
{
    public ObservableCollection<ToastItem> Items { get; } = new();

    private static readonly TimeSpan DefaultLife = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ErrorLife   = TimeSpan.FromSeconds(7);

    public void Info   (string title, string message = "") => Push(new ToastItem { Kind = ToastKind.Info,    Title = title, Message = message }, DefaultLife);
    public void Success(string title, string message = "") => Push(new ToastItem { Kind = ToastKind.Success, Title = title, Message = message }, DefaultLife);
    public void Warning(string title, string message = "") => Push(new ToastItem { Kind = ToastKind.Warning, Title = title, Message = message }, DefaultLife);
    public void Error  (string title, string message = "") => Push(new ToastItem { Kind = ToastKind.Error,   Title = title, Message = message }, ErrorLife);

    /// <summary>
    /// Push a sticky toast with an action button. The toast does NOT auto-
    /// dismiss — the user dismisses it explicitly via the X, or it removes
    /// itself once <paramref name="action"/> has run. Use for things like
    /// "Update available" where we don't want the user to miss the prompt.
    /// </summary>
    public ToastItem PushAction(ToastKind kind, string title, string message, string actionLabel, Action action)
    {
        var item = new ToastItem
        {
            Kind        = kind,
            Title       = title,
            Message     = message,
            ActionLabel = actionLabel,
            Action      = action,
        };
        Push(item, DefaultLife);
        return item;
    }

    public void Dismiss(ToastItem item)
    {
        RunOnUi(() => Items.Remove(item));
    }

    private void Push(ToastItem item, TimeSpan life)
    {
        RunOnUi(() =>
        {
            Items.Add(item);
            // Cap visible toasts so a buggy loop can't flood the screen.
            while (Items.Count > 6) Items.RemoveAt(0);

            // Sticky toasts (those with an action) wait for the user — no
            // auto-dismiss timer. Otherwise auto-remove after `life`.
            if (item.IsSticky) return;

            DispatcherTimer.RunOnce(() =>
            {
                if (Items.Contains(item)) Items.Remove(item);
            }, life);
        });
    }

    private static void RunOnUi(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }
}
