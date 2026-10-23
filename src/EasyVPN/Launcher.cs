using System.Diagnostics;

namespace EasyVPN;

/// <summary>Opens a facility's links. They come from the gateway, so only web pages and Remote Desktop are opened.</summary>
public static class Launcher
{
    public static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new ArgumentException($"'{url}' is not a link.");
        switch (uri.Scheme)
        {
            case "rdp":
                RemoteDesktop(uri.Port > 0 ? $"{uri.Host}:{uri.Port}" : uri.Host);
                break;
            case "http" or "https":
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                break;
            default:
                throw new ArgumentException($"EasyVPN opens web pages and rdp:// links, not '{url}'.");
        }
    }

    public static void RemoteDesktop(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        Process.Start(new ProcessStartInfo("mstsc.exe") { ArgumentList = { $"/v:{host}" } });
    }
}
