using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace EasyVPN;

public partial class MainWindow : Window
{
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

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => WindowFrame.ToggleMaximized(this);

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

    private void FitMaximized() => WindowFrame.FitMaximized(this, Root);
}
