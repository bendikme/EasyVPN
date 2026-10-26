using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace EasyVPN;

/// <summary>How the notification-area icon looks: the connection at a glance.</summary>
public enum TrayLook
{
    /// <summary>Nothing connected, and all well: the app's own icon.</summary>
    Idle,

    /// <summary>The service or the gateway cannot be reached: grey.</summary>
    Off,

    /// <summary>Connecting or disconnecting: amber, breathing.</summary>
    Busy,

    /// <summary>Connected: green, with a tick in the shield.</summary>
    Connected,

    /// <summary>Interrupted or failed: red, with an exclamation mark in the shield.</summary>
    Problem,
}

/// <summary>
/// The app's icon in the colour of the connection: its tile recoloured, and a tick or an
/// exclamation mark for the keyhole, so the state does not rest on colour alone. Made from the
/// icon's own frame for the size the notification area uses (the shield as drawn, at every
/// scale), once for each look.
/// </summary>
public static class TrayIcons
{
    /// <summary>Frames in one breath of <see cref="TrayLook.Busy"/>.</summary>
    public const int BusyFrames = 8;

    private static readonly Uri IconUri = new("pack://application:,,,/Assets/easyvpn.ico");
    private static readonly Dictionary<(TrayLook, int, int), Icon> Made = [];

    // The tile's colour for each look, against the icon's blue: a hue, how much of its saturation,
    // and how much lighter (amber is light by nature, green dark).
    private static readonly Dictionary<TrayLook, (double Hue, double Saturation, double Light)> Tints = new()
    {
        [TrayLook.Off] = (215, 0.12, 0.04),
        [TrayLook.Busy] = (36, 1.0, 0.08),
        [TrayLook.Connected] = (145, 0.78, -0.04),
        [TrayLook.Problem] = (356, 0.8, 0.06),
    };

    /// <param name="frame">For <see cref="TrayLook.Busy"/>: where in the breath, 0 to <see cref="BusyFrames"/> - 1.</param>
    /// <param name="size">In pixels; the notification area's own by default.</param>
    public static Icon Get(TrayLook look, int frame = 0, int? size = null)
    {
        var pixels = size ?? System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        frame = look == TrayLook.Busy ? frame % BusyFrames : 0;
        if (Made.TryGetValue((look, frame, pixels), out var icon)) return icon;

        using var image = Frame(pixels);
        if (Tints.TryGetValue(look, out var tint))
        {
            var breath = look == TrayLook.Busy ? 0.12 * (1 - Math.Cos(2 * Math.PI * frame / BusyFrames)) / 2 : 0;
            Recolour(image, tint.Hue, tint.Saturation, tint.Light + breath);
            var deep = FromHsl(tint.Hue, tint.Saturation, 0.42 + tint.Light);
            if (look == TrayLook.Connected) Mark(image, deep, tick: true);
            else if (look == TrayLook.Problem) Mark(image, deep, tick: false);
        }
        // The handle lives as long as the app: there are only a few of these.
        return Made[(look, frame, pixels)] = Icon.FromHandle(image.GetHicon());
    }

    /// <summary>The icon's frame for <paramref name="size"/>: the one that size, or the next larger, scaled.</summary>
    private static Bitmap Frame(int size)
    {
        using var stream = System.Windows.Application.GetResourceStream(IconUri).Stream;
        using var file = new MemoryStream();
        stream.CopyTo(file);
        var bytes = file.ToArray();

        // An .ico is a directory of frames; this one's are PNGs.
        var frames = Enumerable.Range(0, BitConverter.ToUInt16(bytes, 4))
            .Select(i => (Entry: 6 + 16 * i, Width: bytes[6 + 16 * i] is 0 ? 256 : (int)bytes[6 + 16 * i]))
            .ToList();
        var larger = frames.Where(f => f.Width >= size).ToList();
        var pick = larger.Count > 0 ? larger.MinBy(f => f.Width) : frames.MaxBy(f => f.Width);
        var length = BitConverter.ToInt32(bytes, pick.Entry + 8);
        var offset = BitConverter.ToInt32(bytes, pick.Entry + 12);

        using var png = new MemoryStream(bytes, offset, length);
        file.Position = 0;
        using var source = bytes[offset] == 0x89 ? new Bitmap(png) : new Icon(file, size, size).ToBitmap();
        var image = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(image);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, 0, 0, size, size);
        return image;
    }

    /// <summary>The blue made another colour; the white shield, which has no colour, stays white.</summary>
    private static void Recolour(Bitmap image, double hue, double saturation, double light)
    {
        var area = new Rectangle(0, 0, image.Width, image.Height);
        var data = image.LockBits(area, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] == 0) continue;
                var (_, s, l) = ToHsl(pixels[i + 2], pixels[i + 1], pixels[i]);
                if (s < 0.02) continue;
                // Lighter or darker as much as the pixel is coloured: the edges blended into the shield follow.
                var (r, g, b) = FromHsl(hue, s * saturation, Math.Clamp(l + light * s, 0, 1));
                (pixels[i + 2], pixels[i + 1], pixels[i]) = (r, g, b);
            }
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            image.UnlockBits(data);
        }
    }

    /// <summary>A tick, or an exclamation mark, where the keyhole is.</summary>
    private static void Mark(Bitmap image, (byte R, byte G, byte B) colour, bool tick)
    {
        var n = image.Width;
        using var g = Graphics.FromImage(image);

        // The keyhole goes: the shield is white all round it.
        using (var white = new SolidBrush(Color.White))
        {
            int left = (int)Math.Floor(0.40 * n), right = (int)Math.Ceiling(0.60 * n);
            int top = (int)Math.Floor(0.355 * n), bottom = (int)Math.Ceiling(0.655 * n);
            g.FillRectangle(white, left, top, right - left, bottom - top);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        var ink = Color.FromArgb(colour.R, colour.G, colour.B);
        if (tick)
        {
            using var pen = new Pen(ink, Math.Max(1.6f, n * 0.085f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, [new PointF(0.385f * n, 0.505f * n), new PointF(0.47f * n, 0.59f * n), new PointF(0.62f * n, 0.42f * n)]);
        }
        else
        {
            using var pen = new Pen(ink, Math.Max(1.6f, n * 0.09f));
            g.DrawLine(pen, 0.5f * n, 0.36f * n, 0.5f * n, 0.5f * n);
            using var brush = new SolidBrush(ink);
            var radius = Math.Max(0.8f, n * 0.05f);
            g.FillEllipse(brush, 0.5f * n - radius, 0.605f * n - radius, 2 * radius, 2 * radius);
        }
    }

    private static (double H, double S, double L) ToHsl(byte red, byte green, byte blue)
    {
        double r = red / 255.0, g = green / 255.0, b = blue / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    private static (byte R, byte G, byte B) FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
