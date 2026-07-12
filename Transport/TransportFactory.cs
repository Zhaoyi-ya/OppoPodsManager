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

    /// <summary>不指定目标：枚举全部候选并逐台尝试，不再由系统枚举顺序决定目标。</summary>
    public static IPodTransport Create() => Create(0, null);

    /// <summary>
    /// 指定目标耳机地址创建传输栈（多耳机切换用）。
    /// targetAddr==0 时逐台尝试全部候选；非 0 时只连接指定地址。
    /// </summary>
    public static IPodTransport Create(ulong targetAddr, string? name)
    {
        if (Override != null)
        {
            Log.D("FACTORY", "Create: 使用注入的 Override 传输实现");
            return Override();
        }

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            Log.D("FACTORY", $"Create: Windows 平台 -> 逐设备 Winsock SPP, GATT 回退 (目标={(targetAddr == 0 ? "自动候选" : targetAddr.ToString("X12"))})");
            if (targetAddr == 0)
                return new CandidateTransport(DeviceDiscovery.ListCandidates, CreateWindowsTarget);
            // 定向：Winsock SPP + GATT 回退，均锁定同一台设备
            return CreateWindowsTarget(targetAddr, name ?? ("耳机 " + targetAddr.ToString("X12")));
        }
#endif

#if LINUX
		if (OperatingSystem.IsLinux())
		{
			// Linux: RFCOMM (AF_BLUETOOTH socket) 优先，BLE GATT (BlueZ D-Bus) 回退
			Log.D("FACTORY", $"Create: Linux 平台 -> RFCOMM 优先, GATT 回退 (目标={(targetAddr == 0 ? "任意" : targetAddr.ToString("X12"))})");
			if (targetAddr == 0)
			{
				return new FallbackTransport(
					() => new LinuxRfcommStreamTransport(),
					() => new LinuxGattTransport());
			}
			// 定向：两条链路都注入固定地址定位器，锁定同一台设备（多耳机切换）
			return new FallbackTransport(
				() => new LinuxRfcommStreamTransport(new FixedDeviceLocator(targetAddr, name)),
				() => new LinuxGattTransport(new FixedDeviceLocator(targetAddr, name)));
		}

		Log.D("FACTORY", "Create: 当前平台无传输实现,抛出 PlatformNotSupportedException");
#endif
		throw new PlatformNotSupportedException(
			"当前平台暂无硬件传输实现。请为该平台实现 IPodTransport（如 macOS IOBluetooth），并在 TransportFactory 中按平台分支返回。");
    }

#if WINDOWS
    private static IPodTransport CreateWindowsTarget(ulong addr, string name)
    {
        var locator = new FixedDeviceLocator(addr, name);
        // Winsock SPP 直接按地址连接，不依赖易失效的 WinRT RFCOMM 服务代理。
        // GATT 仅作为同一设备的后备路径，绝不重新选择其它设备。
        return new FallbackTransport(
            () => new SppTransport(locator),
            () => new WindowsGattTransport(locator));
    }
#endif
}
