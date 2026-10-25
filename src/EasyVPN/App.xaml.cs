using System.Windows;

namespace EasyVPN;

/// <summary>
/// One copy per user, living in the notification area. Starting it again shows the running copy;
/// <c>--tray</c> (used when it starts with Windows) starts it without opening the window.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = "EasyVPN.App";

    private Mutex? _instance;
    private EventWaitHandle? _showRequest;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private TrayMenuWindow? _popup;
    private Hotkey? _hotkey;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, InstanceName, out var first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(InstanceName + ".Show", out var show)) show.Set();
            Shutdown();
            return;
        }
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        ThreadPool.RegisterWaitForSingleObject(_showRequest, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, -1, false);

        base.OnStartup(e);
        ThemeService.Follow();

        var agent = new AgentClient();
        var model = new MainViewModel(agent, UserSettings.Load());
        _window = new MainWindow(model, agent);

        // The tray menu, made after the window so the window stays the application's main one.
        var menu = new TrayMenuViewModel(model, ConfirmSwitch);
        menu.ShowRequested += facility =>
        {
            model.Show(facility);
            ShowWindow();
        };
        _popup = new TrayMenuWindow(menu);
        _popup.OpenRequested += ShowWindow;
        _popup.SettingsRequested += ShowSettings;
        _popup.ExitRequested += Quit;
        _tray = new TrayIcon(model, _popup, ShowWindow, ShowSettings, Quit);
        _hotkey = Hotkey.Register(model.User.Hotkey, _tray.ToggleAtCursor);

        model.UpdateInstalling += () =>
        {
            UpdateRestart.Schedule("EasyVPN.Agent", showWindow: _window.IsVisible);
            Quit();
        };
        if (!e.Args.Contains("--tray")) ShowWindow();
        model.Start();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async void ShowSettings()
    {
        if (_window is null) return;
        ShowWindow();
        await _window.ShowSettingsAsync();
    }

    /// <summary>Asked before the tray menu connects a facility while the gateway carries another: one click there should not drop a connection.</summary>
    private static bool ConfirmSwitch(FacilityViewModel facility, FacilityViewModel? carried) =>
        MessageBox.Show(
            $"The gateway is connected to {carried?.Name ?? "another facility"}. Disconnect it, for everyone using it, and connect {facility.Name}?",
            "EasyVPN", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    private void Quit()
    {
        _hotkey?.Dispose();
        _tray?.Dispose();
        _popup?.CloseForGood();
        _window?.CloseForGood();
        Shutdown();
    }
}
