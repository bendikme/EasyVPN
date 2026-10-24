using System.IO;

namespace EasyVPN.GatewayApp;

/// <summary>
/// What went wrong in the app that nothing else caught: written to
/// <c>%LOCALAPPDATA%\EasyVPN\EasyVPN.GatewayApp.log</c>, so a failure leaves something to look at
/// rather than an app that is simply gone.
/// </summary>
public static class CrashLog
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyVPN", "EasyVPN.GatewayApp.log");

    public static void Write(object? error, string where)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            // Kept small: the newest failure is the one that matters.
            if (File.Exists(Path) && new FileInfo(Path).Length > 1_000_000) File.Move(Path, Path + ".old", overwrite: true);
            File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {where} (process {Environment.ProcessId}, {string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Take(2))}){Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write it; there is nothing more to be done.
        }
    }
}
