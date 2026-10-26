using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EasyVPN;

/// <summary>
/// The tray menu, as DynatecRDM's quick launch. Made once and kept: showing it is a Show() and a
/// placement, and it hides itself as soon as it loses the focus.
/// </summary>
public partial class TrayMenuWindow : Window
{
    private const double PopupWidth = 420;
    private const double PopupMaxHeight = 620;
    private const double PopupMinHeight = 120;
    private const double Gap = 8;

    private static readonly DoubleAnimation FadeIn = Fade();

    private readonly TrayMenuViewModel _model;
    private readonly FirstFrameCloak _cloak;
    private WorkArea _area;
    private int _anchorX;
    private int _anchorY;
    private bool _fromTray;
    private bool _hiding;
    private bool _closing;
    private bool _refitQueued;

    public TrayMenuWindow(TrayMenuViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        _cloak = new FirstFrameCloak(this);

        model.CloseRequested += HidePopup;
        model.RowsChanged += OnRowsChanged;
        Deactivated += (_, _) => HidePopup();
        RowsList.SelectionChanged += (_, _) =>
        {
            if (RowsList.SelectedItem is { } selected) RowsList.ScrollIntoView(selected);
        };
    }

    /// <summary>The main window: Open EasyVPN.</summary>
    public event Action? OpenRequested;

    /// <summary>Settings, in the main window.</summary>
    public event Action? SettingsRequested;

    /// <summary>Exit EasyVPN.</summary>
    public event Action? ExitRequested;

    /// <summary>When the popup last went away, so a click on the tray icon that took it away does not bring it back.</summary>
    public long LastHiddenTicks { get; private set; }

    /// <summary>
    /// Shows the popup at a point on the screen, in device pixels, kept to the work area of the
    /// monitor there and in that monitor's scale. From the tray it runs from the icon to the
    /// right edge of the screen; otherwise it opens beside the point, on whichever side has room.
    /// </summary>
    public void ShowAt(int x, int y, bool fromTray)
    {
        _area = WorkArea.At(x, y);
        _anchorX = x;
        _anchorY = y;
        _fromTray = fromTray;
        _model.PrepareForShow();

        PlaceLogical();
        if (!IsVisible)
        {
            _cloak.BeforeShow();
            Show();
        }

        // Laid out before the first frame, then put right in device pixels, so a monitor with
        // another scale cannot leave the popup half off the screen.
        UpdateLayout();
        PlaceLogical();
        PlaceDevice();

        Activate();
        if (new WindowInteropHelper(this).Handle is var handle && handle != IntPtr.Zero) ForceForeground(handle);
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        Root.BeginAnimation(OpacityProperty, FadeIn);
    }

    /// <summary>Puts the popup away, without closing it.</summary>
    public void HidePopup()
    {
        if (_hiding || !IsVisible) return;
        _hiding = true;
        try
        {
            Hide();
            _cloak.Hidden();
            LastHiddenTicks = Environment.TickCount64;
            _model.OnHidden();
        }
        finally
        {
            _hiding = false;
        }
    }

    /// <summary>Really closes it, when EasyVPN exits.</summary>
    public void CloseForGood()
    {
        _closing = true;
        _cloak.Hidden();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true;
            HidePopup();
        }
        base.OnClosing(e);
    }

    // ---------------------------------------------------------------- keys and clicks

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var browsing = _model.SearchText.Length == 0;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                HidePopup();
                break;
            case Key.Down:
                e.Handled = true;
                _model.MoveSelection(1);
                break;
            case Key.Up:
                e.Handled = true;
                _model.MoveSelection(-1);
                break;
            case Key.PageDown:
                e.Handled = true;
                _model.MoveSelection(8);
                break;
            case Key.PageUp:
                e.Handled = true;
                _model.MoveSelection(-8);
                break;
            // Right and Left open and close groups, while there is no search for them to move in.
            case Key.Right when browsing:
                e.Handled = true;
                _model.OpenSelected(true);
                break;
            case Key.Left when browsing:
                e.Handled = true;
                _model.OpenSelected(false);
                break;
            case Key.Enter when e.OriginalSource is not ButtonBase:
                e.Handled = true;
                _model.ActivateSelected();
                break;
            // Delete ends the connection chosen, the way it closes a window in Alt+Tab - only when
            // the search box would do nothing with it, so the search can still be edited.
            case Key.Delete when Keyboard.Modifiers == ModifierKeys.None && !SearchWantsDelete():
                if (_model.DisconnectSelected()) e.Handled = true;
                break;
        }
        base.OnPreviewKeyDown(e);
    }

    private bool SearchWantsDelete() =>
        SearchBox.IsKeyboardFocusWithin && (SearchBox.SelectionLength > 0 || SearchBox.CaretIndex < SearchBox.Text.Length);

    private void OnRowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: ListRow row }) return;
        e.Handled = true;
        _model.Click(row);
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        HidePopup();
        OpenRequested?.Invoke();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        HidePopup();
        SettingsRequested?.Invoke();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        HidePopup();
        ExitRequested?.Invoke();
    }

    // ---------------------------------------------------------------- placement

    /// <summary>Rows came or went: the popup fits its height to them, once things settle.</summary>
    private void OnRowsChanged()
    {
        if (!IsVisible || _refitQueued) return;
        _refitQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _refitQueued = false;
            if (!IsVisible) return;
            PlaceLogical();
            PlaceDevice();
        });
    }

    private void PlaceLogical()
    {
        // Only ever placed where ShowAt said.
        if (_area.Scale <= 0) return;
        var scale = _area.Scale;
        var height = _area.Height / scale;
        var maxHeight = Math.Max(PopupMinHeight, Math.Min(PopupMaxHeight, height - 2 * Gap));
        MaxHeight = maxHeight;
        Width = PopupWidth;
        Height = Math.Clamp(ContentHeight(maxHeight), PopupMinHeight, maxHeight);

        Place(_anchorX / scale, _anchorY / scale, _area.Left / scale, _area.Top / scale, _area.Width / scale, height,
            Width, Height, Gap, _fromTray, out var left, out var top);
        Left = left;
        Top = top;
    }

    /// <summary>
    /// The last word, in device pixels. WPF turns Left and Top into pixels with the scale the
    /// window has now, which is the wrong one the moment it moves to another monitor.
    /// </summary>
    private void PlaceDevice()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (_area.Scale <= 0 || handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return;
        double width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return;

        Place(_anchorX, _anchorY, _area.Left, _area.Top, _area.Width, _area.Height,
            width, height, Gap * _area.Scale, _fromTray, out var x, out var y);
        int left = (int)Math.Round(x), top = (int)Math.Round(y);
        if (left != rect.Left || top != rect.Top) SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>
    /// Beside the point, on whichever side has room, then kept inside the work area: a taskbar on
    /// any edge, without knowing which. From the tray the popup starts at the icon instead, and
    /// the right edge of the screen stops it, so it sits in the corner by the clock.
    /// </summary>
    private static void Place(
        double pointX, double pointY,
        double areaLeft, double areaTop, double areaWidth, double areaHeight,
        double width, double height, double gap, bool fromTray,
        out double x, out double y)
    {
        x = fromTray ? pointX
            : pointX > areaLeft + areaWidth / 2 ? pointX - width - gap : pointX + gap;
        y = pointY > areaTop + areaHeight / 2 ? pointY - height - gap : pointY + gap;
        x = Math.Clamp(x, areaLeft, Math.Max(areaLeft, areaLeft + areaWidth - width));
        y = Math.Clamp(y, areaTop, Math.Max(areaTop, areaTop + areaHeight - height));
    }

    private double ContentHeight(double maxHeight)
    {
        Root.Measure(new Size(PopupWidth, maxHeight));
        var height = Root.DesiredSize.Height;
        return height > 1 && double.IsFinite(height) ? Math.Min(height, maxHeight)
            : ActualHeight > 1 ? Math.Min(ActualHeight, maxHeight)
            : maxHeight;
    }

    private static DoubleAnimation Fade()
    {
        var animation = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(90))) { FillBehavior = FillBehavior.Stop };
        animation.Freeze();
        return animation;
    }

    /// <summary>
    /// A window shown from the tray is not given the foreground by Windows, and without it the
    /// popup would not see the click elsewhere that should put it away. Borrowing the input of
    /// the window that has it lets it take the foreground.
    /// </summary>
    private static void ForceForeground(IntPtr handle)
    {
        var self = GetCurrentThreadId();
        var other = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var attached = other != 0 && other != self && AttachThreadInput(self, other, true);
        try
        {
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }
        finally
        {
            if (attached) AttachThreadInput(self, other, false);
        }
    }

    // ---------------------------------------------------------------- the monitor

    /// <summary>The work area of a monitor, in device pixels, and its scale.</summary>
    private readonly record struct WorkArea(int Left, int Top, int Width, int Height, double Scale)
    {
        public static WorkArea At(int x, int y)
        {
            var monitor = MonitorFromPoint(new Point32 { X = x, Y = y }, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            {
                var area = SystemParameters.WorkArea;
                return new WorkArea((int)area.Left, (int)area.Top, (int)area.Width, (int)area.Height, 1);
            }
            var scale = 1.0;
            try
            {
                if (GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0) scale = dpi / 96.0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }
            var work = info.Work;
            return new WorkArea(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top, scale);
        }
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x1, SwpNoZOrder = 0x4, SwpNoActivate = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point32 point, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint to, bool on);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
