using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OppoPodsManager.Application.Brands;
using OppoPodsManager.Application.Connection;
using OppoPodsManager.Application.Control;
using OppoPodsManager.Application.Discovery;
using OppoPodsManager.Application.State;
using OppoPodsManager.Brands.Oppo;
using OppoPodsManager.Core.Brands;
using OppoPodsManager.Core.Communication;
using OppoPodsManager.Core.Connections;
using OppoPodsManager.Core.Devices;
using OppoPodsManager.Platforms.Common;
#if WINDOWS
using OppoPodsManager.Platforms.Windows;
#endif
#if LINUX
using OppoPodsManager.Platforms.Linux;
#endif

namespace OppoPodsManager.Composition;

/// <summary>
/// Composition root for the layered architecture (Core/Application/Platforms/Brands).
/// </summary>
public static class ServiceRegistration
{
    public static HeadsetControlService? Current { get; private set; }

    public static HeadsetControlService CreateHeadsetControlService(
        Func<CancellationToken, ValueTask<IReadOnlyList<RawDeviceCandidate>>>? discover = null,
        Func<RawDeviceCandidate, ConnectionProfile, CancellationToken, ValueTask<IRawConnection>>? open = null)
    {
        IDeviceDiscovery discovery = discover is null
            ? CreateDefaultDiscovery()
            : new FuncDeviceDiscovery(discover);

        Func<RawDeviceCandidate, IReadOnlyList<ConnectionProfile>> profiles = CreateDefaultProfiles();

        IPlatformConnectionFactory connections = open is null
            ? CreateDefaultConnectionFactory(profiles)
            : new ConfiguredConnectionFactory(profiles, open);

        IBrandRegistry brands = new StaticBrandRegistry(
        [
            new OppoBrandConnector(),
        ]);

        var orchestrator = new ConnectionOrchestrator(
            discovery,
            connections,
            brands.Connectors);

        var store = new HeadsetStateStore();
        var sessions = new HeadsetSessionCoordinator(orchestrator, store);
        var control = new HeadsetControlService(
            sessions,
            new DeviceDiscoveryService(discovery));
        Current = control;
        return control;
    }

    private static Func<RawDeviceCandidate, IReadOnlyList<ConnectionProfile>> CreateDefaultProfiles()
    {
#if WINDOWS
        return WindowsRawConnectionProfiles.ForCandidate;
#elif LINUX
        return LinuxRawConnectionProfiles.ForCandidate;
#else
        return static _ => Array.Empty<ConnectionProfile>();
#endif
    }

    private static IDeviceDiscovery CreateDefaultDiscovery()
    {
#if WINDOWS
        return new WindowsConnectedDeviceDiscovery();
#else
        // Linux discovery placeholder until BlueZ raw discovery is wired.
        return new FuncDeviceDiscovery(static _ =>
            ValueTask.FromResult<IReadOnlyList<RawDeviceCandidate>>(Array.Empty<RawDeviceCandidate>()));
#endif
    }

    private static IPlatformConnectionFactory CreateDefaultConnectionFactory(
        Func<RawDeviceCandidate, IReadOnlyList<ConnectionProfile>> profiles)
    {
#if WINDOWS
        return new WindowsPlatformConnectionFactory();
#else
        return new ConfiguredConnectionFactory(profiles, static (_, _, _) =>
            throw new PlatformNotSupportedException("当前平台尚未迁移原始连接实现。"));
#endif
    }
}
