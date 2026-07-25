using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using OppoPodsManager.Core.Communication;
using OppoPodsManager.Core.Connections;
using OppoPodsManager.Core.Devices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace OppoPodsManager.Platforms.Windows;

/// <summary>
/// Windows raw-device discovery only. Brand confirmation happens after connect via handshake.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsConnectedDeviceDiscovery : IDeviceDiscovery
{
    private const int WinRtTimeoutMs = 4000;
    private static readonly Guid MelodySppUuid = new("0000079A-D102-11E1-9B23-00025B00A5A5");

    public async ValueTask<IReadOnlyList<RawDeviceCandidate>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var merged = new Dictionary<ulong, string>();

        // Prefer Win32 connected enumeration (authoritative fConnected, fewer WinRT races).
        foreach (var (addr, name, connected) in Win32Enumerate())
        {
            if (connected && addr != 0)
                merged[addr] = name;
        }

        // WinRT is only a supplement when Win32 found nothing (e.g. some BLE-only paths).
        // Avoid always opening WinRT BluetoothDevice objects during every refresh.
        if (merged.Count == 0)
        {
            foreach (var (addr, name) in await ListConnectedViaWinRtAsync(cancellationToken).ConfigureAwait(false))
            {
                if (addr != 0 && !merged.ContainsKey(addr))
                    merged[addr] = name;
            }
        }

        var result = new List<RawDeviceCandidate>();
        foreach (var (addr, name) in merged.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
        {
            var hasMelody = HasOppoSppService(addr);
            // Drop phones/PCs/etc. Legacy watcher used the same gate before probing.
            if (!SupportedEarbudIdentity.IsCandidate(name, hasMelody))
                continue;

            result.Add(new RawDeviceCandidate(
                StableId: addr.ToString("X12"),
                PlatformDeviceId: null,
                BluetoothAddress: addr,
                AdvertisedName: string.IsNullOrWhiteSpace(name) ? $"耳机 {addr:X12}" : name,
                ServiceUuids: hasMelody ? new HashSet<Guid> { MelodySppUuid } : new HashSet<Guid>(),
                AvailableTransports: new HashSet<DeviceTransport>
                {
                    DeviceTransport.Rfcomm,
                    DeviceTransport.BluetoothClassic,
                    DeviceTransport.Gatt,
                }));
        }

        return result;
    }

    private static async Task<IReadOnlyList<(ulong addr, string name)>> ListConnectedViaWinRtAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(WinRtTimeoutMs);
            return await EnumerateConnectedAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (ObjectDisposedException)
        {
            // WinRT COM objects can be torn down during process shutdown / radio reset.
            return [];
        }
        catch
        {
            return [];
        }
    }

    private static async Task<List<(ulong addr, string name)>> EnumerateConnectedAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<(ulong addr, string name)>();
        var seen = new HashSet<ulong>();
        string selector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
        var devices = await DeviceInformation.FindAllAsync(selector)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        foreach (var info in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BluetoothDevice? device = null;
            try
            {
                device = await BluetoothDevice.FromIdAsync(info.Id)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                if (device is null || device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                    continue;

                ulong addr = device.BluetoothAddress;
                if (addr == 0 || !seen.Add(addr))
                    continue;

                var name = string.IsNullOrEmpty(info.Name) ? $"耳机 {addr:X12}" : info.Name;
                result.Add((addr, name));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                // Device removed/radio toggled while enumerating.
            }
            catch
            {
            }
            finally
            {
                device?.Dispose();
            }
        }

        return result;
    }

    public static bool HasOppoSppService(ulong address)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Services\{address:X12}");
            if (key is null)
                return false;

            foreach (var serviceName in key.GetSubKeyNames())
            {
                if (serviceName.Contains("0000079A", StringComparison.OrdinalIgnoreCase)
                    || serviceName.Contains("000079A", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static List<(ulong addr, string name, bool connected)> Win32Enumerate()
    {
        var result = new List<(ulong addr, string name, bool connected)>();
        var search = new BluetoothDeviceSearchParams
        {
            dwSize = (uint)Marshal.SizeOf<BluetoothDeviceSearchParams>(),
            fReturnAuthenticated = 1,
            fReturnRemembered = 1,
            fReturnUnknown = 0,
            fReturnConnected = 1,
            fIssueInquiry = 0,
            cTimeoutMultiplier = 0,
            hRadio = IntPtr.Zero,
        };

        var info = new BluetoothDeviceInfo { dwSize = (uint)Marshal.SizeOf<BluetoothDeviceInfo>() };
        IntPtr hFind = IntPtr.Zero;
        try
        {
            hFind = BluetoothFindFirstDevice(ref search, ref info);
            if (hFind == IntPtr.Zero)
                return result;

            do
            {
                string name = ReadName(ref info);
                bool connected = info.fConnected != 0;
                ulong addr = info.Address & 0xFFFFFFFFFFFFUL;
                if (addr != 0)
                    result.Add((addr, name, connected));
                info = new BluetoothDeviceInfo { dwSize = (uint)Marshal.SizeOf<BluetoothDeviceInfo>() };
            }
            while (BluetoothFindNextDevice(hFind, ref info));
        }
        catch
        {
        }
        finally
        {
            if (hFind != IntPtr.Zero)
            {
                try { BluetoothFindDeviceClose(hFind); } catch { }
            }
        }

        return result;
    }

    private static string ReadName(ref BluetoothDeviceInfo info)
    {
        var sb = new StringBuilder(248);
        for (var i = 0; i < 248; i++)
        {
            ushort c = info.szName[i];
            if (c == 0)
                break;
            sb.Append((char)c);
        }

        return sb.ToString();
    }

    [InlineArray(248)]
    private struct NameBuffer { private ushort _e0; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothDeviceInfo
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        public int fConnected;
        public int fRemembered;
        public int fAuthenticated;
        public SystemTime stLastSeen;
        public SystemTime stLastUsed;
        public NameBuffer szName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothDeviceSearchParams
    {
        public uint dwSize;
        public int fReturnAuthenticated;
        public int fReturnRemembered;
        public int fReturnUnknown;
        public int fReturnConnected;
        public int fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr BluetoothFindFirstDevice(
        ref BluetoothDeviceSearchParams pbtsp, ref BluetoothDeviceInfo pbtdi);

    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BluetoothDeviceInfo pbtdi);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(IntPtr hFind);
}
