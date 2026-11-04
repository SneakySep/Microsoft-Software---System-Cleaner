using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WindowsCleaner.App.Theme;

/// <summary>
/// Puts the operating system's own translucent backdrop behind a window.
/// </summary>
/// <remarks>
/// On Windows 11 22H2 and later, the Desktop Window Manager can draw one of its system
/// materials behind a window's whole surface, client area included. Requesting the acrylic
/// variant is what produces the liquid-glass look: the desktop behind the window is blurred
/// and tinted by the system compositor itself, and the app's translucent surfaces from
/// <c>Theme.xaml</c> sit on top of it. Doing this in the compositor rather than inside the
/// app is also why it costs nothing per frame: the blur is not a WPF effect, it is the same
/// material the shell draws under its own flyouts.
///
/// The request degrades by design. On Windows 10, on pre-22H2 builds of Windows 11, and in
/// sessions where the compositor refuses the material (remote desktop, battery savers), the
/// attribute call returns a failure code, and the window simply keeps the opaque surface it
/// was defined with. A caller therefore ends up with either real glass or the pre-glass
/// appearance, never a transparent hole in the screen.
/// </remarks>
internal static class Backdrop
{
    private const int DwmwaSystemBackdropType = 38;

    // DWM_SYSTEMBACKDROP_TYPE: 2 = Mica, tuned for main windows; 3 = acrylic, the more
    // translucent material DWM reserves for transient surfaces. Acrylic is the glassier of
    // the two, and this app is a dashboard over a desktop, so it is the one asked for.
    private const int DwmsbtAcrylic = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Lets the system material show through <paramref name="window"/>'s surface.
    /// </summary>
    /// <remarks>
    /// Call once from <see cref="Window.SourceInitialized"/>, when the Win32 handle exists but
    /// nothing has been rendered yet, so there is no flash of either surface. The window keeps
    /// its opaque <c>Brush.Window</c> background from XAML; it is swapped for a transparent
    /// one only after DWM has agreed to draw the material behind it. A transparent WPF brush
    /// still hit-tests, so input behaviour is unchanged either way.
    /// </remarks>
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var backdropType = DwmsbtAcrylic;

        if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, sizeof(int)) != 0)
        {
            return;
        }

        // With the material accepted, the window must stop painting an opaque surface over
        // it, or the glass would never be visible. Plain transparent rather than null: a
        // null background is not hit-testable, a transparent one is.
        window.Background = Brushes.Transparent;
    }
}