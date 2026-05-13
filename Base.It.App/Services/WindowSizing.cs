using Avalonia.Controls;

namespace Base.It.App.Services;

/// <summary>
/// Defensive window-sizing helper used by every standalone window the app
/// opens (Preview, Batch, Error detail). Without this, the static
/// <c>Width</c>/<c>Height</c> a window is declared with can exceed the
/// screen's working area — chopping off the title bar / close button on
/// laptops or vertically-constrained monitors. This clamps to a sensible
/// fraction of the working area and centers, so a window always opens
/// fully visible regardless of the user's display.
/// </summary>
public static class WindowSizing
{
    /// <summary>
    /// Caps the window's Width/Height to <paramref name="maxFraction"/> of
    /// the primary screen's working area (defaults to 90%) and re-centers
    /// it inside that area. Safe to call from <c>Opened</c>: the working
    /// area is finalized by then.
    /// </summary>
    public static void ClampToWorkingArea(Window window, double maxFraction = 0.9)
    {
        if (window is null) return;
        var screen = window.Screens?.ScreenFromVisual(window)
                  ?? window.Screens?.Primary;
        if (screen is null) return;

        var wa = screen.WorkingArea;
        var scale = window.DesktopScaling <= 0 ? 1.0 : window.DesktopScaling;

        // WorkingArea is in pixels; Width/Height are in DIPs. Divide by the
        // scale factor to compare apples-to-apples.
        var maxW = (wa.Width  / scale) * maxFraction;
        var maxH = (wa.Height / scale) * maxFraction;

        if (window.Width  > maxW) window.Width  = maxW;
        if (window.Height > maxH) window.Height = maxH;

        // Re-center inside the working area so the clamp doesn't leave the
        // window pinned at (0,0) when the OS placed it elsewhere.
        window.Position = new Avalonia.PixelPoint(
            wa.X + (int)((wa.Width  - window.Width  * scale) / 2),
            wa.Y + (int)((wa.Height - window.Height * scale) / 2));
    }
}
