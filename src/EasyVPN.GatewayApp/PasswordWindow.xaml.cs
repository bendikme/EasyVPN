using System.Runtime.InteropServices;
using System.Windows;

namespace EasyVPN.GatewayApp;

/// <summary>
/// What <c>EasyVPN.Gateway.exe protect</c> does, without a console: DPAPI for this machine, which
/// any account on it may encrypt with, while only SYSTEM and administrators can read facilities.json.
/// </summary>
public partial class PasswordWindow : Window
{
    public PasswordWindow()
    {
        InitializeComponent();
        WindowFrame.Dialog(this);
        SourceInitialized += (_, _) => ThemeService.TitleBar(this);
        Loaded += (_, _) => FirstBox.Focus();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var ready = FirstBox.Password.Length > 0 && FirstBox.Password == SecondBox.Password;
        ResultBox.Text = ready ? Gateway.Secret.Protect(FirstBox.Password) : "";
        HintText.Text = SecondBox.Password.Length > 0 && !ready ? "The two differ." : "";
        CopyButton.IsEnabled = ready;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ResultBox.Text);
        }
        catch (COMException)
        {
            // Another program has the clipboard open; a second click will do.
        }
    }
}
