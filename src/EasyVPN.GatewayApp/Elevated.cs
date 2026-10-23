using System.ComponentModel;
using System.Diagnostics;

namespace EasyVPN.GatewayApp;

/// <summary>
/// What takes an administrator - the access key, facilities.json, starting the service - is
/// started as one, after Windows asks (UAC). The app itself never runs elevated.
/// </summary>
public static class Elevated
{
    private const int Cancelled = 1223;

    /// <returns>False when the person said no.</returns>
    public static bool Start(string file, string arguments, bool hidden = false)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            })?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
            return false;
        }
    }

    /// <summary>Opens something the ordinary way: a web page, Network Connections.</summary>
    public static void Open(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}
