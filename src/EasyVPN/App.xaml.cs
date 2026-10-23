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
        _tray = new TrayIcon(model, ShowWindow, Quit);
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

    private void Quit()
    {
        _tray?.Dispose();
        _window?.CloseForGood();
        Shutdown();
    }
}
