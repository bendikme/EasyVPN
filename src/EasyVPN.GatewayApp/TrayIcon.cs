using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EasyVPN.GatewayApp;

/// <summary>
/// The notification-area icon: the gateway at a glance in its colour and its tooltip, and the
/// window a click away. The menu is a WPF menu, so it wears the app's theme.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly GatewayViewModel _model;
    private readonly Action _open;
    private readonly Action _exit;
    private readonly DispatcherTimer _breath = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private TrayLook _look = TrayLook.Idle;
    private int _frame;

    public TrayIcon(GatewayViewModel model, Action open, Action exit)
    {
        _model = model;
        _open = open;
        _exit = exit;

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = TrayIcons.Get(TrayLook.Idle),
            Text = "EasyVPN Gateway",
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) _open();
            else if (e.Button == System.Windows.Forms.MouseButtons.Right) ShowMenu();
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
    }

    /// <summary>The connection's colour; amber breathes while it connects.</summary>
    private void Show(TrayLook look)
    {
        if (look == _look) return;
        _look = look;
        _frame = 0;
        _icon.Icon = TrayIcons.Get(look);
        if (look == TrayLook.Busy) _breath.Start();
        else _breath.Stop();
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(new MenuItem { Header = "Open EasyVPN Gateway", Command = new RelayCommand(_open) });
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
