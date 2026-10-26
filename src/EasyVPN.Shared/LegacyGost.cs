using System.Diagnostics;

namespace EasyVPN;

/// <summary>
/// Up to 0.4 the tunnel ran in gost.exe, beside the service. One left running across the update
/// would hold the tunnel's adapter name, so a service stops any still running from its own folder
/// once, at startup.
/// </summary>
public static class LegacyGost
{
    public static void KillStrays()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "gost.exe");
        foreach (var process in Process.GetProcessesByName("gost"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                        process.Kill();
                }
                catch (Exception)
                {
                    // Gone already, or not ours to look at.
                }
            }
        }
    }
}
