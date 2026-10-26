using System.ComponentModel;
using System.Windows;

namespace EasyVPN.GatewayApp;

/// <summary>
/// The facility editor, in a copy of the app started as administrator (<c>--facilities</c>). A
/// password box cannot be bound, so it is handled here: what is typed goes to the facility shown,
/// and the box empties when another facility is shown or the password is saved.
/// </summary>
public partial class FacilitiesWindow : Window
{
    private readonly FacilitiesViewModel _model;
    private FacilityDraft? _shown;
    private bool _clearing;

    public FacilitiesWindow(string? select)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.TitleBar(this);
        _model = new FacilitiesViewModel(select, Ask);
        DataContext = _model;
        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FacilitiesViewModel.Selected)) ShowDraft(_model.Selected);
        };
        ShowDraft(_model.Selected);
        // Edited in Notepad meanwhile: show what the file says now.
        Activated += (_, _) => _model.ReloadIfChanged();
    }

    private void ShowDraft(FacilityDraft? draft)
    {
        if (_shown is not null) _shown.PropertyChanged -= OnShownChanged;
        _shown = draft;
        if (_shown is not null) _shown.PropertyChanged += OnShownChanged;
        ClearPassword();
    }

    private void OnShownChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FacilityDraft.NewPassword) && _shown?.NewPassword.Length == 0) ClearPassword();
    }

    private void ClearPassword()
    {
        if (PasswordBox.Password.Length == 0) return;
        _clearing = true;
        PasswordBox.Clear();
        _clearing = false;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_clearing && _shown is not null) _shown.NewPassword = PasswordBox.Password;
    }

    private void OnRemovePasswordClick(object sender, RoutedEventArgs e) => _shown?.RemovePassword();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var button = (FrameworkElement)sender;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private void OnEncryptPasswordClick(object sender, RoutedEventArgs e) => new PasswordWindow { Owner = this }.ShowDialog();

    // ---------------------------------------------------------------- the window's own title strip

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => WindowFrame.ToggleMaximized(this);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowFrame.FitMaximized(this, Root);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        WindowFrame.FitMaximized(this, Root);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        WindowFrame.FitMaximized(this, Root);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_model.IsDirty)
        {
            var answer = MessageBox.Show(this, "Save the changes to the facilities?", Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !_model.Save())) e.Cancel = true;
        }
        base.OnClosing(e);
    }

    private bool Ask(string question) =>
        MessageBox.Show(this, question, Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
}
