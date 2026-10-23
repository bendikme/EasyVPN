using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EasyVPN;

/// <summary>
/// A TAP-Windows adapter (OpenVPN's tap-windows6 driver, "TAP-Windows Adapter V9"), opened by its
/// name in Network Connections: Ethernet frames in and out, one per read or write. Windows sees
/// its cable plugged in only while <see cref="SetConnected"/> says so, and only one program can
/// have it open at a time.
/// </summary>
public sealed class TapAdapter : IDisposable
{
    /// <summary>Comfortably more than any Ethernet frame, VLAN tag included.</summary>
    public const int MaxFrame = 16 * 1024;

    // TAP_WIN_IOCTL_SET_MEDIA_STATUS = CTL_CODE(FILE_DEVICE_UNKNOWN, 6, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint SetMediaStatus = (0x22 << 16) | (6 << 2);

    private readonly SafeFileHandle _handle;
    private readonly FileStream _frames;

    private TapAdapter(SafeFileHandle handle)
    {
        _handle = handle;
        _frames = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
    }

    /// <summary>The TAP adapter of that name, if this PC has one.</summary>
    public static NetworkInterface? Find(string name) =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(nic =>
            string.Equals(nic.Name, name, StringComparison.OrdinalIgnoreCase)
            && nic.Description.StartsWith("TAP-Windows", StringComparison.OrdinalIgnoreCase));

    /// <exception cref="InvalidOperationException">There is no such adapter.</exception>
    /// <exception cref="IOException">Another program has it open.</exception>
    public static TapAdapter Open(string name)
    {
        var nic = Find(name) ?? throw new InvalidOperationException($"This PC has no TAP adapter called \"{name}\".");
        try
        {
            var handle = File.OpenHandle($@"\\.\Global\{nic.Id}.tap", FileMode.Open, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous);
            return new TapAdapter(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"The \"{name}\" adapter is in use by another program, or this one may not open it ({ex.Message})", ex);
        }
    }

    /// <summary>Plugs the adapter's virtual cable in, or pulls it out.</summary>
    public void SetConnected(bool connected)
    {
        var status = connected ? 1 : 0;
        // Like OpenVPN, without an OVERLAPPED: this request completes at once.
        if (!DeviceIoControl(_handle, SetMediaStatus, ref status, sizeof(int), ref status, sizeof(int), out _, 0))
            throw new IOException($"The TAP adapter would not change its media status (error {Marshal.GetLastPInvokeError()}).");
    }

    public ValueTask<int> ReadAsync(Memory<byte> frame, CancellationToken ct) => _frames.ReadAsync(frame, ct);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken ct) => _frames.WriteAsync(frame, ct);

    public void Dispose()
    {
        try
        {
            SetConnected(false);
        }
        catch (IOException)
        {
            // Closing the handle unplugs it as well.
        }
        _frames.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref int input, int inputSize, ref int output, int outputSize, out int returned, nint overlapped);
}
