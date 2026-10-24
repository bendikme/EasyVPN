using System.Diagnostics;
using System.IO;
using System.Text;

namespace EasyVPN;

/// <summary>
/// An update replaces the app too, so just before the service installs one the app starts this
/// watcher and exits. The watcher - Windows PowerShell, which the installer leaves alone - waits
/// for the service to stop and start again, then starts the app again as it was: in the
/// notification area, or with its window open. If the service never stops (the installer gave up
/// early), it brings the app back after a few minutes anyway.
/// </summary>
public static class UpdateRestart
{
    public static void Schedule(string service, bool showWindow)
    {
        var exe = Environment.ProcessPath!.Replace("'", "''");
        var arguments = showWindow ? "" : " -ArgumentList '--tray'";
        var script = $$"""
            $start = Get-Date
            $stopped = $false
            while ($true) {
                Start-Sleep -Seconds 2
                $minutes = ((Get-Date) - $start).TotalMinutes
                if ((Get-Service '{{service}}' -ErrorAction SilentlyContinue).Status -ne 'Running') { $stopped = $true }
                elseif ($stopped -or $minutes -gt 3) { break }
                if ($minutes -gt 15) { break }
            }
            if (Test-Path -LiteralPath '{{exe}}') { Start-Process -FilePath '{{exe}}'{{arguments}} }
            """;
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) },
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();
    }
}
