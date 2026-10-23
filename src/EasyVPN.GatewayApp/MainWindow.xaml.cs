using System.ComponentModel;
using System.Windows;

namespace EasyVPN.GatewayApp;

public partial class MainWindow : Window
{
    private bool _exiting;

    public MainWindow(GatewayViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        SourceInitialized += (_, _) => ThemeService.TitleBar(this);
        // The header's dot follows the service; a style trigger would need a converter for one dot.
        model.PropertyChanged += (_, _) =>
            ServiceDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, model.ServiceOnline ? "SuccessBrush" : "DangerBrush");
    }

    /// <summary>Closing the window keeps the app in the notification area; this is the real close.</summary>
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

    private void OnEncryptPasswordClick(object sender, RoutedEventArgs e) => new PasswordWindow { Owner = this }.ShowDialog();
}
