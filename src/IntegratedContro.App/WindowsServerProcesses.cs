using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;

namespace IntegratedContro.App;

// Native process/listener details stay at the Windows app boundary.
internal static class WindowsServerProcesses
{
    public static async Task<IDisposable> AcquireStartupLock(string data, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(Path.Combine(data, "app-server-startup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
            {
                if (timer.Elapsed >= timeout) throw new TimeoutException();
                await Task.Delay(100);
            }
        }
    }

    public static bool HostIsLocked(string data)
    {
        try { using var file = new FileStream(Path.Combine(data, "host.lock"), FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (FileNotFoundException) { return false; }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return true; }
    }

    public static Process Start(string executable, string[] arguments, string directory)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = directory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("서버 프로세스를 시작하지 못했습니다.");
    }

    public static bool IsExecutable(int pid, string path)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return string.Equals(process.MainModule?.FileName, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public static int? ListenerProcess(Uri endpoint)
    {
        var address = endpoint.Host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(endpoint.Host.Trim('[', ']'));
        // MIB_TCP(6)ROW_OWNER_PID layout, Windows IP Helper. Query listeners only.
        var ipv6 = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        var family = ipv6 ? 23 : 2;
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
        if (result != 122 && result != 0) throw new System.ComponentModel.Win32Exception((int)result);
        var table = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(table, ref size, false, family, 3, 0);
            if (result != 0) throw new System.ComponentModel.Win32Exception((int)result);
            var count = Marshal.ReadInt32(table);
            var stride = ipv6 ? 56 : 24;
            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(table, 4 + i * stride);
                var portOffset = ipv6 ? 20 : 8;
                var port = (Marshal.ReadByte(row, portOffset) << 8) | Marshal.ReadByte(row, portOffset + 1);
                if (port != endpoint.Port) continue;
                var bytes = new byte[ipv6 ? 16 : 4];
                Marshal.Copy(IntPtr.Add(row, ipv6 ? 0 : 4), bytes, 0, bytes.Length);
                var bound = new IPAddress(bytes);
                if (bound.Equals(address) || bound.Equals(ipv6 ? IPAddress.IPv6Any : IPAddress.Any))
                {
                    var pid = Marshal.ReadInt32(row, ipv6 ? 52 : 20);
                    try { using var process = Process.GetProcessById(pid); if (!process.HasExited) return pid; }
                    catch (ArgumentException) { /* TCP table may briefly retain a just-exited process. */ }
                }
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int family, int tableClass, uint reserved);
}
