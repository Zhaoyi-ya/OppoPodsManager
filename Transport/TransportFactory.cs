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

    /// <summary>不指定目标：连接第一个匹配的耳机（单设备旧行为）。</summary>
    public static IPodTransport Create() => Create(0, null);

    /// <summary>
    /// 指定目标耳机地址创建传输栈（多耳机切换用）。
    /// targetAddr==0 时等价于旧的"第一个匹配"行为。
    /// 非 0 时：RFCOMM StreamSocket 按地址过滤，Spp/Gatt 注入 FixedDeviceLocator 精确指向该设备。
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
            // 经典 SPP (RFCOMM) 优先——多数 OPPO 耳机在 Windows 下只暴露经典口。
            // 依次：WinRT StreamSocket → Winsock P/Invoke 回退 → BLE GATT 回退。
            Log.D("FACTORY", $"Create: Windows 平台 -> RFCOMM(StreamSocket) 优先, Winsock 次之, GATT 回退 (目标={(targetAddr == 0 ? "任意" : targetAddr.ToString("X12"))})");
            if (targetAddr == 0)
            {
                return new FallbackTransport(
                    () => new WindowsRfcommStreamTransport(),
                    () => new SppTransport(),
                    () => new WindowsGattTransport());
            }
            // 定向：三条链路都锁定同一台设备
            return new FallbackTransport(
                () => new WindowsRfcommStreamTransport(targetAddr),
                () => new SppTransport(new FixedDeviceLocator(targetAddr, name)),
                () => new WindowsGattTransport(new FixedDeviceLocator(targetAddr, name)));
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
}
