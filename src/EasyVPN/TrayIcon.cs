using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EasyVPN;

/// <summary>
/// The notification-area icon: the connection at a glance in its colour (amber and breathing
/// while connecting, green connected, red when it went wrong) and its tooltip, Windows
/// notifications when it changes, the tray menu - every facility, searchable - on a click, and
/// the window, Settings and Exit on a right-click.
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
    private readonly Notices _notices = new();
    private readonly DispatcherTimer _breath = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private TrayLook _look = TrayLook.Idle;
    private int _frame;

    public TrayIcon(MainViewModel model, TrayMenuWindow popup, Action open, Action settings, Action exit)
    {
        _model = model;
        _popup = popup;
        _open = open;
        _settings = settings;
        _exit = exit;

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = TrayIcons.Get(TrayLook.Idle),
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
        // A notification clicked: the window, at the facility it was about.
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_model.Active is { } facility) _model.Show(facility);
            _open();
        };
        _breath.Tick += (_, _) =>
        {
            _frame = (_frame + 1) % TrayIcons.BusyFrames;
            _icon.Icon = TrayIcons.Get(TrayLook.Busy, _frame);
        };

        model.PropertyChanged += (_, _) =>
        {
            _icon.Text = model.TrayText.Length > 127 ? model.TrayText[..127] : model.TrayText;
            Show(model.TrayLook);
        };
        model.Polled += () =>
        {
            foreach (var notice in _notices.Next(model.Connection, model.AskedRecently))
                if (model.User.Notifications) Notify(notice);
        };
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

    private void Show(TrayLook look)
    {
        if (look == _look) return;
        _look = look;
        _frame = 0;
        _icon.Icon = TrayIcons.Get(look);
        if (look == TrayLook.Busy) _breath.Start();
        else _breath.Stop();
    }

    private void Notify(Notice notice)
    {
        var level = notice.Level switch
        {
            NoticeLevel.Error => System.Windows.Forms.ToolTipIcon.Error,
            NoticeLevel.Warning => System.Windows.Forms.ToolTipIcon.Warning,
            _ => System.Windows.Forms.ToolTipIcon.Info,
        };
        // Windows shows it as a notification from EasyVPN, and keeps it in the notification centre.
        _icon.ShowBalloonTip(5000, notice.Title, notice.Text, level);
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
        _breath.Stop();
        _icon.Visible = false;
        _icon.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);
}
