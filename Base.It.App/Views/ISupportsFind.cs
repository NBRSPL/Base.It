namespace Base.It.App.Views;

/// <summary>
/// Implemented by views that have a "find / filter" surface on their
/// pane. Drives the window-level Ctrl+F overlay: pressing Ctrl+F opens
/// a small floating find box (browser-style); typing into it calls
/// <see cref="ApplyFind"/> on the active view; closing the overlay
/// (Esc / X) clears the filter by calling <see cref="ApplyFind"/>
/// with an empty string.
///
/// Modelled on the browser's Ctrl+F: the find box is a transient piece
/// of chrome the window owns, not a permanent input on the page.
/// </summary>
public interface ISupportsFind
{
    /// <summary>
    /// Apply (or clear, when <paramref name="text"/> is null/empty) the
    /// pane's find/filter expression. Implementations should be
    /// idempotent and cheap — typing fires this once per keystroke.
    /// </summary>
    void ApplyFind(string? text);

    /// <summary>
    /// Current find expression, used to pre-populate the overlay when it
    /// reopens (so users see what was last typed, can Esc / X to clear).
    /// </summary>
    string CurrentFindText { get; }
}
