using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace EasyVPN;

/// <summary>
/// For a window with a strip of its own instead of the Windows title bar (Themes\Chrome.xaml), as
/// both apps' main windows are.
/// </summary>
public static class WindowFrame
{
    private const int SmCxFrame = 32;
    private const int SmCyFrame = 33;
    private const int SmCxPaddedBorder = 92;

    /// <summary>The height of the strip that stands in for the title bar.</summary>
    public const double CaptionHeight = 44;

    /// <summary>
    /// Windows makes a maximized window larger than the screen by its frame, which a window
    /// without one would lose off every edge; pull the content in by that much (from DynatecRDM).
    /// Call it when the window's handle is made, its state changes and its DPI changes.
    /// </summary>
    public static void FitMaximized(Window window, FrameworkElement root)
    {
        var inset = new Thickness(0);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (window.WindowState == WindowState.Maximized && hwnd != 0)
        {
            var dpi = GetDpiForWindow(hwnd);
            var padded = GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
            var scale = VisualTreeHelper.GetDpi(window);
            var x = (GetSystemMetricsForDpi(SmCxFrame, dpi) + padded) / scale.DpiScaleX;
            var y = (GetSystemMetricsForDpi(SmCyFrame, dpi) + padded) / scale.DpiScaleY;
            inset = new Thickness(x, y, x, y);
        }
        root.Margin = inset;
        if (WindowChrome.GetWindowChrome(window) is { } chrome) chrome.CaptionHeight = CaptionHeight + inset.Top;
    }

    public static void ToggleMaximized(Window window)
    {
        if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
        else SystemCommands.MaximizeWindow(window);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
