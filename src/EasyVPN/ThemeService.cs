using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace EasyVPN;

/// <summary>
/// Follows Windows' app mode (Settings > Personalisation > Colours), live, by swapping the palette
/// - the first merged dictionary in App.xaml. Everything else reads the palette through
/// DynamicResource, as in DynatecRDM, whose design system this is.
/// </summary>
public static class ThemeService
{
    private const int DwmUseImmersiveDarkMode = 20;

    public static bool IsDark { get; private set; }

    public static void Follow()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General) Application.Current.Dispatcher.BeginInvoke(Apply);
        };
    }

    /// <summary>A window with a Windows title bar gets the dark one while the app is dark.</summary>
    public static void TitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        var dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    private static void Apply()
    {
        IsDark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
        Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"Themes/Palette.{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative),
        };
        foreach (Window window in Application.Current.Windows) TitleBar(window);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
