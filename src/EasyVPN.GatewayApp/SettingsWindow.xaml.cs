using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EasyVPN.GatewayApp;

/// <summary>
/// Settings, in a copy of the app started as administrator (<c>--settings</c>). Password boxes
/// cannot be bound, so what is typed goes to its section from here, and the boxes empty once saved.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _model;

    public SettingsWindow()
    {
        InitializeComponent();
        WindowFrame.Dialog(this);
        SourceInitialized += (_, _) => ThemeService.TitleBar(this);
        _model = new SettingsViewModel(question =>
            MessageBox.Show(this, question, Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK);
        _model.Saved += () =>
        {
            foreach (var box in PasswordBoxes(this)) box.Clear();
        };
        DataContext = _model;
    }

    private static IEnumerable<PasswordBox> PasswordBoxes(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is PasswordBox box) yield return box;
            foreach (var inner in PasswordBoxes(child)) yield return inner;
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if (box.DataContext is ConnectorSection section) section.NewPassword = box.Password;
    }

    private void OnRemovePasswordClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ConnectorSection section) section.RemovePassword();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_model.IsDirty)
        {
            var answer = MessageBox.Show(this, "Save the changes to the settings?", Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !_model.Save())) e.Cancel = true;
        }
        base.OnClosing(e);
    }
}
