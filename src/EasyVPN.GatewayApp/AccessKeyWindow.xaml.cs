using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace EasyVPN.GatewayApp;

/// <summary>
/// The access key, read from gateway.json - which only SYSTEM and administrators can read, so the
/// app shows it from a copy of itself started as administrator.
/// </summary>
public partial class AccessKeyWindow : Window
{
    private sealed record GatewayFile(string? AccessKey);

    public AccessKeyWindow()
    {
        InitializeComponent();
        WindowFrame.Dialog(this);
        SourceInitialized += (_, _) => ThemeService.TitleBar(this);
        try
        {
            var file = DataDirectory.ReadJson<GatewayFile>(Path.Combine(DataDirectory.Get("Gateway"), "gateway.json"));
            KeyBox.Text = file?.AccessKey ?? throw new InvalidOperationException("There is no access key yet: the EasyVPN Gateway service makes it when it first starts.");
            KeyBox.Focus();
            KeyBox.SelectAll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            ErrorText.Text = ex is UnauthorizedAccessException ? "It takes an administrator to see the access key." : ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            CopyButton.IsEnabled = false;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(KeyBox.Text);
        }
        catch (COMException)
        {
            // Another program has the clipboard open; a second click will do.
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
