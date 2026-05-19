namespace Base.It.App.Views;

/// <summary>
/// Implemented by views that have a visible search/filter textbox on
/// their page. Ctrl+F at the window level routes here so the keyboard
/// shortcut just focuses that existing textbox — no hidden overlay,
/// no parallel "find" state that silently mirrors the visible filter.
///
/// Earlier the contract was an <c>ApplyFind(string)</c> that piped text
/// from a popup overlay into the page's filter property. That made
/// Ctrl+F and the visible filter textbox appear to be two separate
/// inputs while actually editing the same backing field — confusing.
/// The new shape exposes a single action: "put keyboard focus on this
/// page's filter textbox" — the textbox itself is the only search UI.
/// </summary>
public interface ISupportsFind
{
    /// <summary>
    /// Focus this page's visible filter / search textbox so the user
    /// can start typing immediately. No-op (and no <c>Handled</c>) if
    /// the page doesn't currently have a focusable filter (e.g. nothing
    /// is selected yet that would expose one).
    /// </summary>
    /// <returns>
    /// <c>true</c> if focus was placed on a textbox, <c>false</c>
    /// otherwise. Callers can use the return to decide whether to
    /// fall through to a different Ctrl+F handler.
    /// </returns>
    bool FocusFindBox();
}
