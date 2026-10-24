using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EasyVPN.GatewayApp;

public partial class MainWindow : Window
{
    private bool _exiting;

    // The activity log keeps its newest line in view, unless someone has scrolled up to read.
    private bool _following = true;
    private bool _scrollPending;

    public MainWindow(GatewayViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        SourceInitialized += (_, _) => ThemeService.TitleBar(this);
        // The header's dot follows the service; a style trigger would need a converter for one dot.
        model.PropertyChanged += (_, _) =>
            ServiceDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, model.ServiceOnline ? "SuccessBrush" : "DangerBrush");

        // Scrolled once the list has taken the new lines in, and once for all of them: scrolling from
        // inside the change itself makes the list measure itself before it has heard of the line.
        model.Activity.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add || !_following || _scrollPending) return;
            _scrollPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _scrollPending = false;
                if (model.Activity.Count > 0) ActivityList.ScrollIntoView(model.Activity[^1]);
            });
        };
        ActivityList.Loaded += (_, _) =>
        {
            if (Find<ScrollViewer>(ActivityList) is { } scroll)
                scroll.ScrollChanged += (_, e) =>
                {
                    // Lines being added move the end, not the reader.
                    if (e.ExtentHeightChange == 0) _following = scroll.VerticalOffset >= scroll.ScrollableHeight - 2;
                };
            if (model.Activity.Count > 0) ActivityList.ScrollIntoView(model.Activity[^1]);
        };
    }

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as T ?? Find<T>(child)) is { } found) return found;
        }
        return null;
    }

    private void OnCopyActivityClick(object sender, RoutedEventArgs e) => CopyActivity();

    private void OnCopyActivity(object sender, ExecutedRoutedEventArgs e) => CopyActivity();

    /// <summary>The lines selected, or else all of them, with their dates, ready to paste into a message.</summary>
    private void CopyActivity()
    {
        var model = (GatewayViewModel)DataContext;
        var lines = ActivityList.SelectedItems.Count > 0 ? ActivityList.SelectedItems.Cast<LogLine>().OrderBy(l => l.Time) : model.Activity.AsEnumerable();
        var text = string.Join(Environment.NewLine, lines.Select(l => $"{l.Time.LocalDateTime:yyyy-MM-dd HH:mm:ss}  {l.Text}"));
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (COMException)
        {
            // Another program has the clipboard open; a second click will do.
        }
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

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        var model = (GatewayViewModel)DataContext;
        if (model.CanInstallUpdate && model.IsBusy && MessageBox.Show(this,
                "Installing the update disconnects the facility for every workstation, and the gateway restarts. Install it now?",
                "EasyVPN Gateway", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        model.UpdateCommand.Execute(null);
    }
}
