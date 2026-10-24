using System.Windows;

namespace EasyVPN.GatewayApp;

/// <summary>
/// The EasyVPN Gateway app: one copy per user of the gateway's desktop, in the notification area,
/// started at sign-in with <c>--tray</c>. Starting it again shows the running copy.
/// <c>--access-key</c> is the app started again as administrator, to show the access key and nothing else.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = "EasyVPN.GatewayApp";

    private Mutex? _instance;
    private EventWaitHandle? _showRequest;
    private MainWindow? _window;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeService.Follow();

        if (e.Args.Contains("--access-key"))
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            new AccessKeyWindow().Show();
            return;
        }

        _instance = new Mutex(true, InstanceName, out var first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(InstanceName + ".Show", out var show)) show.Set();
            Shutdown();
            return;
        }
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        ThreadPool.RegisterWaitForSingleObject(_showRequest, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, -1, false);

        var model = new GatewayViewModel(new LocalGateway());
        _window = new MainWindow(model);
        _tray = new TrayIcon(model, ShowWindow, Quit);
        model.UpdateInstalling += () =>
        {
            UpdateRestart.Schedule("EasyVPN.Gateway", showWindow: _window.IsVisible);
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

    private void Quit()
    {
        _tray?.Dispose();
        _window?.CloseForGood();
        Shutdown();
    }
}
