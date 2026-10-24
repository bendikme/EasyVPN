using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace EasyVPN;

public partial class MainWindow : Window
{
    private const int SmCxFrame = 32;
    private const int SmCyFrame = 33;
    private const int SmCxPaddedBorder = 92;
    private const double CaptionHeight = 44;

    private readonly MainViewModel _model;
    private readonly AgentClient _agent;
    private bool _exiting;

    public MainWindow(MainViewModel model, AgentClient agent)
    {
        _model = model;
        _agent = agent;
        InitializeComponent();
        DataContext = model;
    }

    /// <summary>Closing the window keeps EasyVPN in the notification area; this is the real close.</summary>
    public void CloseForGood()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_agent) { Owner = this };
        if (dialog.ShowDialog() == true) await _model.ReloadAsync();
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_model.CanInstallUpdate && _model.IsBusy && MessageBox.Show(this,
                "Installing the update disconnects this PC from the facility, and EasyVPN restarts. Install it now?",
                "EasyVPN", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _model.UpdateCommand.Execute(null);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        FitMaximized();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        FitMaximized();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        FitMaximized();
    }

    /// <summary>
    /// Windows makes a maximized window larger than the screen by its frame, which a window
    /// without one would lose off every edge; pull the content in by that much (from DynatecRDM).
    /// </summary>
    private void FitMaximized()
    {
        var inset = new Thickness(0);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (WindowState == WindowState.Maximized && hwnd != 0)
        {
            var dpi = GetDpiForWindow(hwnd);
            var padded = GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
            var scale = VisualTreeHelper.GetDpi(this);
            var x = (GetSystemMetricsForDpi(SmCxFrame, dpi) + padded) / scale.DpiScaleX;
            var y = (GetSystemMetricsForDpi(SmCyFrame, dpi) + padded) / scale.DpiScaleY;
            inset = new Thickness(x, y, x, y);
        }
        Root.Margin = inset;
        if (WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CaptionHeight = CaptionHeight + inset.Top;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
