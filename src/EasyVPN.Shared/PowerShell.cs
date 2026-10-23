using System.Diagnostics;

namespace EasyVPN;

/// <summary>
/// Runs a script in Windows PowerShell, which every Windows has. Always as a file: an encoded
/// command reports its errors as CLIXML, which is no use in a log.
/// </summary>
public static class PowerShell
{
    private static readonly string Exe =
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// Runs <paramref name="path"/> and returns its exit code, passing every line it prints to
    /// <paramref name="output"/>. Values go in <paramref name="environment"/> rather than on the
    /// command line, which any user on the machine can read. A script still running after
    /// <paramref name="timeout"/> is stopped, and reports -1.
    /// </summary>
    public static async Task<int> RunFileAsync(
        string path,
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        Action<string> output,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo(Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in (string[])["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path, .. args])
            start.ArgumentList.Add(arg);
        foreach (var (name, value) in environment)
            start.Environment[name] = value;

        using var process = new Process { StartInfo = start };
        DataReceivedEventHandler print = (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) output(e.Data.Trim());
        };
        process.OutputDataReceived += print;
        process.ErrorDataReceived += print;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (ct.IsCancellationRequested) throw;
            output($"{Path.GetFileName(path)} was stopped after {timeout.TotalSeconds:0} s");
            return -1;
        }
    }
}
