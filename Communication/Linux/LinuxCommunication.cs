using System.Diagnostics;
using System.Runtime.InteropServices;
using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Communication.Linux;

// Linux 平台按已连接的 BlueZ 设备轮询；品牌筛选仍由发现实现负责。
public sealed class LinuxBluetoothDiscovery : IDeviceDiscoveryMonitor
{
    private readonly object _gate = new();
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private IReadOnlyList<DeviceCandidate> _lastSnapshot = [];
    private long _generation;
    private bool _disposed;

    public event EventHandler<DeviceCandidatesChangedEventArgs>? DevicesChanged;

    public Task<IReadOnlyList<DeviceCandidate>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => EnumerateCandidates(), cancellationToken);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _monitorTask is not null)
                return;
            _monitorCancellation = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorAsync(_monitorCancellation.Token));
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            cancellation = _monitorCancellation;
            _monitorCancellation = null;
            _monitorTask = null;
            _lastSnapshot = [];
        }
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await DiscoverAsync(cancellationToken);
                var changed = false;
                lock (_gate)
                {
                    if (_disposed)
                        return;
                    changed = !_lastSnapshot.Select(item => item.StableId)
                        .SequenceEqual(snapshot.Select(item => item.StableId));
                    if (changed)
                        _lastSnapshot = snapshot;
                }
                if (changed)
                {
                    var generation = Interlocked.Increment(ref _generation);
                    DevicesChanged?.Invoke(this, new DeviceCandidatesChangedEventArgs(snapshot, generation));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                global::OppoPodsManager.Control.Subsystems.Logging.ApplicationLog.Current?.Error("Bluetooth", "Linux 设备监听失败。", exception);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    private static IReadOnlyList<DeviceCandidate> EnumerateCandidates()
    {
        var result = new List<DeviceCandidate>();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "bluetoothctl",
            Arguments = "devices Paired",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null)
            return result;

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(3000);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !TryParseAddress(parts[1], out var address))
                continue;

            var name = parts.Length == 3 ? parts[2].Trim() : $"耳机 {address:X12}";
            if (!TryGetConnectedServiceIds(address, out var serviceIds))
                continue;

            result.Add(new DeviceCandidate(
                address.ToString("X12"),
                address.ToString("X12"),
                address.ToString("X12"),
                name,
                serviceIds,
                [LinuxConnectionFactory.TransportName]));
        }
        return result
            .GroupBy(candidate => candidate.StableId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // 从 bluetoothctl 的已连接设备信息中提取系统登记的 RFCOMM 服务 UUID。
    private static bool TryGetConnectedServiceIds(ulong address, out IReadOnlyList<Guid> serviceIds)
    {
        serviceIds = [];
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "bluetoothctl",
            Arguments = $"info {FormatAddress(address)}",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null)
            return false;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(3000);
        if (!output.Contains("Connected: yes", StringComparison.OrdinalIgnoreCase))
            return false;

        var parsed = new HashSet<Guid>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var start = line.LastIndexOf('(');
            var end = line.LastIndexOf(')');
            if (start < 0 || end <= start)
                continue;
            if (Guid.TryParse(line[(start + 1)..end], out var serviceId))
                parsed.Add(serviceId);
        }
        serviceIds = parsed.ToArray();
        return serviceIds.Count > 0;
    }

    private static bool TryParseAddress(string text, out ulong address)
        => ulong.TryParse(text.Replace(":", string.Empty).Replace("-", string.Empty),
            System.Globalization.NumberStyles.HexNumber, null, out address);

    private static string FormatAddress(ulong address)
        => string.Join(":", Enumerable.Range(0, 6)
            .Select(index => ((address >> ((5 - index) * 8)) & 0xFF).ToString("X2")));
}

public sealed class LinuxConnectionFactory : IConnectionFactory
{
    public const string TransportName = "rfcomm";

    public string Transport => TransportName;

    public async Task<IRawConnection> OpenAsync(
        DeviceCandidate candidate,
        ConnectionOptions options,
        CancellationToken cancellationToken)
    {
        var connection = new LinuxRfcommConnection(candidate);
        try
        {
            await connection.ConnectAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

internal sealed class LinuxRfcommConnection : IRawConnection
{
    private const string Libc = "libc";
    private const int AfBluetooth = 31;
    private const int SockStream = 1;
    private const int BtProtoRfcomm = 3;
    private const int FGetFl = 3;
    private const int FSetFl = 4;
    private const int ONonBlock = 2048;
    private const int EAgain = 11;

    private readonly DeviceCandidate _candidate;
    private readonly object _socketGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _socket = -1;
    private CancellationTokenSource? _readCancellation;
    private Task? _readTask;
    private int _connected;
    private int _disposed;

    public LinuxRfcommConnection(DeviceCandidate candidate)
    {
        _candidate = candidate;
    }

    public bool IsConnected => Volatile.Read(ref _connected) != 0;
    public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
    public event EventHandler? Disconnected;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!ulong.TryParse(_candidate.BluetoothAddress, System.Globalization.NumberStyles.HexNumber, null, out var address))
            throw new InvalidOperationException("RFCOMM requires a Bluetooth address.");

        var connectedSocket = await Task.Run(() => ScanChannels(address, cancellationToken), cancellationToken);
        lock (_socketGate)
        {
            _socket = connectedSocket;
            Volatile.Write(ref _connected, 1);
            _readCancellation = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoopAsync(_readCancellation.Token));
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var bytes = data.ToArray();
            var offset = 0;
            while (offset < bytes.Length)
            {
                int socket;
                lock (_socketGate) socket = _socket;
                var remaining = offset == 0 ? bytes : bytes[offset..];
                var written = write(socket, remaining, (IntPtr)remaining.Length).ToInt64();
                if (written <= 0)
                    throw new IOException($"RFCOMM write failed: errno={Marshal.GetLastWin32Error()}.");
                offset += (int)written;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var wasConnected = Interlocked.Exchange(ref _connected, 0) != 0;
        _readCancellation?.Cancel();
        CloseSocket();
        if (_readTask is not null)
        {
            try { await _readTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (OperationCanceledException) { }
        }
        _readCancellation?.Dispose();
        _readCancellation = null;
        _readTask = null;
        if (wasConnected)
            Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await DisconnectAsync(CancellationToken.None);
        _writeGate.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                int socket;
                lock (_socketGate) socket = _socket;
                var count = read(socket, buffer, (IntPtr)buffer.Length).ToInt64();
                if (count > 0)
                {
                    DataReceived?.Invoke(this, buffer.AsMemory(0, (int)count).ToArray());
                    continue;
                }
                if (count < 0 && Marshal.GetLastWin32Error() == EAgain)
                {
                    await Task.Delay(30, cancellationToken);
                    continue;
                }
                break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            if (Interlocked.Exchange(ref _connected, 0) != 0 && !cancellationToken.IsCancellationRequested)
                Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private static int ScanChannels(ulong address, CancellationToken cancellationToken)
    {
        for (var channel = 1; channel <= 30; channel++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = socket_create(AfBluetooth, SockStream, BtProtoRfcomm);
            if (socket < 0)
                continue;
            var endpoint = BuildAddress(address, channel);
            if (connect(socket, ref endpoint, (uint)Marshal.SizeOf<SockAddrRc>()) == 0)
            {
                fcntl(socket, FSetFl, fcntl(socket, FGetFl) | ONonBlock);
                return socket;
            }
            close(socket);
        }
        throw new IOException("No RFCOMM channel is available.");
    }

    private void CloseSocket()
    {
        int socket;
        lock (_socketGate) { socket = _socket; _socket = -1; }
        if (socket >= 0)
            close(socket);
    }

    private static SockAddrRc BuildAddress(ulong address, int channel) => new()
    {
        Family = AfBluetooth,
        B0 = (byte)(address & 0xFF),
        B1 = (byte)((address >> 8) & 0xFF),
        B2 = (byte)((address >> 16) & 0xFF),
        B3 = (byte)((address >> 24) & 0xFF),
        B4 = (byte)((address >> 32) & 0xFF),
        B5 = (byte)((address >> 40) & 0xFF),
        Channel = (byte)channel
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SockAddrRc
    {
        public ushort Family;
        public byte B0, B1, B2, B3, B4, B5;
        public byte Channel;
    }

    [DllImport(Libc, EntryPoint = "socket", SetLastError = true)] private static extern int socket_create(int domain, int type, int protocol);
    [DllImport(Libc, SetLastError = true)] private static extern int connect(int socket, ref SockAddrRc address, uint length);
    [DllImport(Libc, SetLastError = true)] private static extern IntPtr read(int socket, byte[] buffer, IntPtr count);
    [DllImport(Libc, SetLastError = true)] private static extern IntPtr write(int socket, byte[] buffer, IntPtr count);
    [DllImport(Libc, SetLastError = true)] private static extern int close(int socket);
    [DllImport(Libc, SetLastError = true)] private static extern int fcntl(int socket, int command);
    [DllImport(Libc, SetLastError = true)] private static extern int fcntl(int socket, int command, int value);
}
