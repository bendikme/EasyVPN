using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace EasyVPN;

/// <summary>
/// The notification-area icon: the connection at a glance in its tooltip, and the favourite
/// facilities a right-click away. The menu is a WPF menu, so it wears the app's theme.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly MainViewModel _model;
    private readonly Action _open;
    private readonly Action _exit;

    public TrayIcon(MainViewModel model, Action open, Action exit)
    {
        _model = model;
        _open = open;
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
            if (e.Button == System.Windows.Forms.MouseButtons.Left) _open();
            else if (e.Button == System.Windows.Forms.MouseButtons.Right) ShowMenu();
        };
        model.PropertyChanged += (_, _) => _icon.Text = model.TrayText.Length > 127 ? model.TrayText[..127] : model.TrayText;
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };

        // The favourites, or every facility when none is starred yet.
        var facilities = _model.Facilities.Where(f => f.IsFavourite).ToList();
        if (facilities.Count == 0) facilities = [.. _model.Facilities];
        foreach (var facility in facilities.OrderBy(f => f.Customer).ThenBy(f => f.Name).Take(15))
        {
            menu.Items.Add(new MenuItem
            {
                Header = $"{facility.Name}  ({facility.Customer})",
                IsChecked = facility.IsActive && _model.IsBusy,
                Command = _model.ConnectCommand,
                CommandParameter = facility,
            });
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        menu.Items.Add(new MenuItem { Header = "Disconnect", Command = _model.DisconnectCommand, IsEnabled = _model.IsBusy });
        menu.Items.Add(new MenuItem { Header = "Open EasyVPN", Command = new RelayCommand(_open) });
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
