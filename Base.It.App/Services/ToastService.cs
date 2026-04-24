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
    public bool ShowMessage => !string.IsNullOrWhiteSpace(Message);

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

    public void Info   (string title, string message = "") => Push(ToastKind.Info,    title, message, DefaultLife);
    public void Success(string title, string message = "") => Push(ToastKind.Success, title, message, DefaultLife);
    public void Warning(string title, string message = "") => Push(ToastKind.Warning, title, message, DefaultLife);
    public void Error  (string title, string message = "") => Push(ToastKind.Error,   title, message, ErrorLife);

    public void Dismiss(ToastItem item)
    {
        RunOnUi(() => Items.Remove(item));
    }

    private void Push(ToastKind kind, string title, string message, TimeSpan life)
    {
        var item = new ToastItem { Kind = kind, Title = title, Message = message };
        RunOnUi(() =>
        {
            Items.Add(item);
            // Cap visible toasts so a buggy loop can't flood the screen.
            while (Items.Count > 6) Items.RemoveAt(0);

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
