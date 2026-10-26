using System.Windows;

namespace EasyVPN;

/// <summary>Which gateway this PC uses (saved by the agent, for every user) and this user's own choices.</summary>
public partial class SettingsWindow : Window
{
    private readonly AgentClient _agent;
    private readonly UserSettings _user;
    private bool _forgetCertificate;

    public SettingsWindow(AgentClient agent, UserSettings user)
    {
        _agent = agent;
        _user = user;
        InitializeComponent();
        StartWithWindowsBox.IsChecked = UserSettings.StartWithWindows;
        NotificationsBox.IsChecked = user.Notifications;
        SourceInitialized += (_, _) => ThemeService.TitleBar(this);
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var settings = await _agent.SettingsAsync();
            GatewayBox.Text = settings.Gateway ?? "";
            KeyHint.Visibility = settings.HasAccessKey ? Visibility.Visible : Visibility.Collapsed;
            ThumbprintText.Text = settings.Thumbprint ?? "None yet: it is pinned the first time this PC reaches the gateway.";
            ForgetButton.IsEnabled = settings.Thumbprint is not null;
            GatewayBox.Focus();
        }
        catch (AgentException ex)
        {
            ShowError(ex.Message);
            SaveButton.IsEnabled = false;
        }
    }

    private void OnForgetClick(object sender, RoutedEventArgs e)
    {
        _forgetCertificate = true;
        ThumbprintText.Text = "Forgotten: the next certificate the gateway shows will be pinned.";
        ForgetButton.IsEnabled = false;
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            await _agent.SaveSettingsAsync(new AgentSettingsUpdate(GatewayBox.Text, KeyBox.Password, _forgetCertificate));
            UserSettings.StartWithWindows = StartWithWindowsBox.IsChecked == true;
            _user.Notifications = NotificationsBox.IsChecked == true;
            _user.Save();
            DialogResult = true;
        }
        catch (AgentException ex)
        {
            ShowError(ex.Message);
            SaveButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
