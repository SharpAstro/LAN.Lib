using System.Net.Sockets;
using System.Net;
using System.Threading.Tasks;
using System.Linq;
using LAN.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddLanDiscovery_HonoursTheConfiguredDiscoveryPort()
    {
        // Pick a port that is free right now, so the transport really binds it.
        int port;
        using (var probe = new UdpClient())
        {
            probe.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        var services = new ServiceCollection();
        services.AddLanDiscovery(o => { o.ServiceName = "test"; o.DiscoveryPort = port; });
        await using var provider = services.BuildServiceProvider(); // the transport is IAsyncDisposable only

        var transport = provider.GetRequiredService<ILanTransport>().ShouldBeOfType<UdpLanTransport>();
        transport.DiscoveryPort.ShouldBe(port);
        transport.Degradation.ShouldBeNull();
        provider.GetRequiredService<LanDiscoveryOptions>().DiscoveryPort.ShouldBe(port);
    }

    [Fact]
    public void AddLanDiscovery_WiresTransport_Discovery_PeerTable_AndHostedService()
    {
        var services = new ServiceCollection();

        // Register a fake transport first: AddLanDiscovery uses TryAdd, so this leaves it in place and
        // no real UDP socket is bound during the test.
        var fake = new FakeLanBus().CreateNode("127.0.0.1");
        services.AddSingleton<ILanTransport>(fake);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());

        services.AddLanDiscovery(o =>
        {
            o.ServiceName = "tianwen-server";
            o.ServicePort = 1888;
            o.Announce = false;
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<LanDiscoveryOptions>().ServiceName.ShouldBe("tianwen-server");
        provider.GetRequiredService<ILanTransport>().ShouldBeSameAs(fake);

        var discovery = provider.GetRequiredService<LanDiscovery>();
        var peerTable = provider.GetRequiredService<IPeerTable>();
        peerTable.ShouldBeSameAs(discovery); // one instance behind both the concrete type and the interface

        provider.GetServices<IHostedService>()
            .OfType<LanDiscoveryHostedService>()
            .ShouldHaveSingleItem();
    }
}
