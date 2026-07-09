using System;

namespace OppoPodsManager;

/// <summary>
/// 按运行平台选择硬件传输实现。核心/编排层通过本工厂拿 IPodTransport，
/// 不直接依赖任何平台专属类（如 SppTransport 只在 Windows 下可用）。
/// 未来加 Linux/macOS：在此按 OperatingSystem.IsLinux() 等分支返回对应实现。
/// </summary>
public static class TransportFactory
{
    /// <summary>可选注入点：测试或自定义实现时设置；为 null 时按平台自动选择。</summary>
    public static Func<IPodTransport>? Override { get; set; }

    public static IPodTransport Create()
    {
        if (Override != null)
        {
            Log.D("FACTORY", "Create: 使用注入的 Override 传输实现");
            return Override();
        }

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            // 经典 SPP (RFCOMM) 优先——多数 OPPO 耳机在 Windows 下只暴露经典口。
            // 依次：WinRT StreamSocket → Winsock P/Invoke 回退 → BLE GATT 回退。
            Log.D("FACTORY", "Create: Windows 平台 -> RFCOMM(StreamSocket) 优先, Winsock 次之, GATT 回退");
            return new FallbackTransport(
                () => new WindowsRfcommStreamTransport(),
                () => new SppTransport(),
                () => new WindowsGattTransport());
        }
#endif

#if LINUX
		if (OperatingSystem.IsLinux())
		{
			// Linux: RFCOMM (AF_BLUETOOTH socket) 优先，BLE GATT (BlueZ D-Bus) 回退
			Log.D("FACTORY", "Create: Linux 平台 -> RFCOMM 优先, GATT 回退");
			return new FallbackTransport(
				() => new LinuxRfcommStreamTransport(),
				() => new LinuxGattTransport());
		}

		Log.D("FACTORY", "Create: 当前平台无传输实现,抛出 PlatformNotSupportedException");
#endif
		throw new PlatformNotSupportedException(
			"当前平台暂无硬件传输实现。请为该平台实现 IPodTransport（如 macOS IOBluetooth），并在 TransportFactory 中按平台分支返回。");
    }
}
