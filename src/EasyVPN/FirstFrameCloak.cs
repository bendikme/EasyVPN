using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EasyVPN;

/// <summary>
/// Shows a see-through window without its previous picture, as in DynatecRDM. Windows keeps the
/// last frame of a hidden layered window and puts it back on screen the moment the window shows
/// again, before WPF has drawn the new one: the popup would flash up as it last looked, where it
/// last was. The window is cloaked from just before it shows until a frame drawn since has reached
/// the screen; DWM still composes a cloaked window, and it can be active, it is only not shown.
/// </summary>
internal sealed class FirstFrameCloak
{
    private const int DwmwaCloak = 13;

    /// <summary>The first frame draws the window as it now is; the ones after it start once that one is on the screen.</summary>
    private const int FramesToWait = 3;

    private readonly Window _window;
    private readonly DispatcherTimer _fallback;
    private int _generation;
    private int _frames;
    private TimeSpan _lastFrame;
    private bool _cloaked;
    private bool _waiting;

    public FirstFrameCloak(Window window)
    {
        _window = window;
        // Never cloaked longer than this, whatever happens to rendering.
        _fallback = new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _fallback.Tick += (_, _) => Uncloak();
    }

    /// <summary>Right before Show(). Where cloaking is not available the window simply shows as before.</summary>
    public void BeforeShow()
    {
        var handle = new WindowInteropHelper(_window).EnsureHandle();
        if (!SetCloaked(handle, true)) return;

        _cloaked = true;
        var generation = ++_generation;
        StopCounting();
        _fallback.Stop();
        _fallback.Start();

        // Counted from the next turn of the dispatcher: a window's very first Show() draws frames
        // there and then, before it has been placed, and those are not the one that has to show.
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (generation != _generation || !_cloaked) return;
            _frames = 0;
            _waiting = true;
            CompositionTarget.Rendering += OnRendering;
        });
    }

    /// <summary>After Hide(): nothing left waiting, and the window never left cloaked.</summary>
    public void Hidden() => Uncloak();

    private void OnRendering(object? sender, EventArgs e)
    {
        // A render WPF is merely asked for again carries the time of the frame before; only a new frame counts.
        if (e is RenderingEventArgs { RenderingTime: var time })
        {
            if (time == _lastFrame) return;
            _lastFrame = time;
        }
        if (++_frames >= FramesToWait) Uncloak();
    }

    private void StopCounting()
    {
        if (!_waiting) return;
        _waiting = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void Uncloak()
    {
        _generation++;
        _fallback.Stop();
        StopCounting();
        if (!_cloaked) return;
        _cloaked = false;

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle != IntPtr.Zero) SetCloaked(handle, false);
    }

    private static bool SetCloaked(IntPtr handle, bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        try
        {
            return DwmSetWindowAttribute(handle, DwmwaCloak, ref value, sizeof(int)) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
