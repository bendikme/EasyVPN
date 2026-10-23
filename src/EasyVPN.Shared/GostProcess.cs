using System.Diagnostics;
using System.Text.Json;

namespace EasyVPN;

/// <summary>
/// One gost.exe (shipped next to this exe) running a JSON configuration. It is restarted if it
/// exits on its own, and a new configuration restarts it - which also drops every connection
/// it was carrying, the point when the facility behind it changes.
/// </summary>
public sealed class GostProcess(string configPath, Action<string> log)
{
    private static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "gost.exe");
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _sync = new();
    private Process? _process;
    private string? _config;
    private DateTime _started;
    private int _quickExits;

    public bool IsRunning
    {
        get { lock (_sync) return _process is { HasExited: false }; }
    }

    /// <summary>
    /// Runs gost with <paramref name="config"/>, unless it is already running exactly that - or
    /// waiting to be started again with it after exiting.
    /// </summary>
    public void Start(object config)
    {
        var json = JsonSerializer.Serialize(config, ConfigJson);
        lock (_sync)
        {
            if (json == _config && _process is not null) return;
            Kill();
            File.WriteAllText(configPath, json);
            _config = json;
            _quickExits = 0;
            Launch();
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _config = null;
            Kill();
        }
    }

    /// <summary>
    /// A gost left behind by a crash of this service would go on carrying traffic with nobody
    /// watching it. A service calls this once at startup; only its own copy of gost is touched.
    /// </summary>
    public static void KillStrays()
    {
        foreach (var process in Process.GetProcessesByName("gost"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, Exe, StringComparison.OrdinalIgnoreCase))
                        process.Kill();
                }
                catch (Exception)
                {
                    // Gone already, or not ours to look at.
                }
            }
        }
    }

    private void Launch()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(Exe)
            {
                ArgumentList = { "-C", configPath },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };
        process.OutputDataReceived += OnOutput;
        process.ErrorDataReceived += OnOutput;
        process.Exited += OnExited;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _started = DateTime.UtcNow;
    }

    private void Kill()
    {
        if (_process is null) return;
        _process.Exited -= OnExited;
        try
        {
            if (!_process.HasExited) _process.Kill();
            _process.WaitForExit(3000);
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill.
        }
        _process.Dispose();
        _process = null;
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (sender is not Process exited) return;
        TimeSpan delay;
        lock (_sync)
        {
            if (exited != _process || _config is null) return;
            // One that keeps failing straight away (no rights to make the adapter, a port in use)
            // is tried less and less often, up to once a minute.
            _quickExits = DateTime.UtcNow - _started < TimeSpan.FromSeconds(30) ? _quickExits + 1 : 0;
            delay = TimeSpan.FromSeconds(Math.Min(60, 2 << Math.Min(_quickExits, 5)));
            log($"gost exited ({exited.ExitCode}); starting it again in {delay.TotalSeconds:0} s");
        }
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            lock (_sync)
            {
                if (exited != _process || _config is null) return;
                exited.Dispose();
                Launch();
            }
        });
    }

    /// <summary>gost logs JSON lines; the message is the part worth keeping.</summary>
    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        try
        {
            using var line = JsonDocument.Parse(e.Data);
            log($"gost: {line.RootElement.GetProperty("msg").GetString()}");
        }
        catch (Exception)
        {
            log($"gost: {e.Data}");
        }
    }
}
