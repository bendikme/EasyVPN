using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace EasyVPN;

/// <summary>
/// The notification-area icon: the connection at a glance in its tooltip, the tray menu - every
/// facility, searchable - on a click, and the window, Settings and Exit on a right-click.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>A click this soon after the popup went away is the click that took it away.</summary>
    private const long ReopenGuardMs = 250;

    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly MainViewModel _model;
    private readonly TrayMenuWindow _popup;
    private readonly Action _open;
    private readonly Action _settings;
    private readonly Action _exit;

    public TrayIcon(MainViewModel model, TrayMenuWindow popup, Action open, Action settings, Action exit)
    {
        _model = model;
        _popup = popup;
        _open = open;
        _settings = settings;
        _exit = exit;

        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/easyvpn.ico")).Stream;
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize),
            Text = "EasyVPN",
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) Toggle(System.Windows.Forms.Cursor.Position, fromTray: true);
            else if (e.Button == System.Windows.Forms.MouseButtons.Right) ShowMenu();
        };
        // A double click's first click opened the popup and its second, activating the taskbar,
        // took it away again: all a double click does is put it back.
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Left || _popup.IsVisible) return;
            var at = System.Windows.Forms.Cursor.Position;
            _popup.ShowAt(at.X, at.Y, fromTray: true);
        };
        model.PropertyChanged += (_, _) => _icon.Text = model.TrayText.Length > 127 ? model.TrayText[..127] : model.TrayText;
    }

    /// <summary>The tray menu beside the mouse, as the shortcut opens it; or away, when it is up.</summary>
    public void ToggleAtCursor() => Toggle(System.Windows.Forms.Cursor.Position, fromTray: false);

    private void Toggle(System.Drawing.Point at, bool fromTray)
    {
        if (_popup.IsVisible)
        {
            _popup.HidePopup();
            return;
        }
        // Clicking the icon took the focus from the popup, which put it away; the same click must not bring it back.
        if (Environment.TickCount64 - _popup.LastHiddenTicks < ReopenGuardMs) return;
        _popup.ShowAt(at.X, at.Y, fromTray);
    }

    private void ShowMenu()
    {
        _popup.HidePopup();
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(new MenuItem { Header = "Open EasyVPN", Command = new RelayCommand(_open) });
        menu.Items.Add(new MenuItem { Header = "Settings", Command = new RelayCommand(_settings) });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Disconnect", Command = _model.DisconnectCommand, IsEnabled = _model.IsBusy });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Exit", Command = new RelayCommand(_exit) });
        menu.IsOpen = true;

        // A menu from the notification area only closes on a click elsewhere when its window has the foreground.
        if (PresentationSource.FromVisual(menu) is HwndSource source) SetForegroundWindow(source.Handle);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);
}
