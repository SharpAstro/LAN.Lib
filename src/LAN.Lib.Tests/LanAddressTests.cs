using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using LAN.Lib;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

public class LanAddressTests
{
    private const string Service = "pilot-node";

    private static LanDiscovery NewDiscovery(FakeLanTransport transport, FakeTimeProvider time, string peerId, bool announce, string? address = null)
    {
        var options = new LanDiscoveryOptions
        {
            ServiceName = Service,
            ServicePort = 47731,
            NodeName = "Me",
            Announce = announce,
            AnnounceAddress = () => address is null ? null : IPAddress.Parse(address),
        };
        return new LanDiscovery(transport, time, options, new LanIdentity(peerId, "node-1"));
    }

    private static string Announce(string peerId, string? addr, string nodeId = "node-1")
    {
        var props = new Dictionary<string, string> { [LanProtocol.PropNodeId] = nodeId };
        if (addr is not null)
        {
            props[LanProtocol.PropAddress] = addr;
        }
        return LanProtocol.EncodeAnnounce(peerId, Service, 47731, "Rig", props);
    }

    [Fact]
    public async Task APeerHeardFromAVirtualAdapterIsReachedAtItsAnnouncedLanAddress()
    {
        var bus = new FakeLanBus();
        var listener = bus.CreateNode("192.168.1.10");
        using var discovery = NewDiscovery(listener, new FakeTimeProvider(), "me", announce: false);
        await discovery.StartAsync(TestContext.Current.CancellationToken);

        listener.DeliverDatagram(new DiscoveryDatagram(Announce("remote", "192.168.1.20"), IPAddress.Parse("172.28.64.1")));

        discovery.PeersOf(Service).ShouldHaveSingleItem().EndPoint.ShouldBe(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 47731));
    }

    [Fact]
    public async Task ANodeHeardOnTwoAdaptersIsOnePeerAtOneAddress()
    {
        var bus = new FakeLanBus();
        var listener = bus.CreateNode("192.168.1.10");
        using var discovery = NewDiscovery(listener, new FakeTimeProvider(), "me", announce: false);
        await discovery.StartAsync(TestContext.Current.CancellationToken);

        listener.DeliverDatagram(new DiscoveryDatagram(Announce("remote", "192.168.1.20"), IPAddress.Parse("172.28.64.1")));
        listener.DeliverDatagram(new DiscoveryDatagram(Announce("remote", "192.168.1.20"), IPAddress.Parse("192.168.1.20")));

        discovery.PeersOf(Service).ShouldHaveSingleItem().EndPoint.Address.ToString().ShouldBe("192.168.1.20");
    }

    [Fact]
    public async Task A23AnnounceWithoutTheAddressStaysAtItsSenderAddress()
    {
        var bus = new FakeLanBus();
        var listener = bus.CreateNode("192.168.1.10");
        using var discovery = NewDiscovery(listener, new FakeTimeProvider(), "me", announce: false);
        await discovery.StartAsync(TestContext.Current.CancellationToken);

        listener.DeliverDatagram(new DiscoveryDatagram(Announce("remote", addr: null), IPAddress.Parse("172.28.64.1")));

        discovery.PeersOf(Service).ShouldHaveSingleItem().EndPoint.Address.ToString().ShouldBe("172.28.64.1");
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("169.254.3.4")]
    [InlineData("224.0.0.1")]
    [InlineData("not-an-address")]
    public async Task AnUnreachableAnnouncedAddressIsIgnored(string addr)
    {
        var bus = new FakeLanBus();
        var listener = bus.CreateNode("192.168.1.10");
        using var discovery = NewDiscovery(listener, new FakeTimeProvider(), "me", announce: false);
        await discovery.StartAsync(TestContext.Current.CancellationToken);

        listener.DeliverDatagram(new DiscoveryDatagram(Announce("remote", addr), IPAddress.Parse("172.28.64.1")));

        discovery.PeersOf(Service).ShouldHaveSingleItem().EndPoint.Address.ToString().ShouldBe("172.28.64.1");
    }

    [Fact]
    public async Task APublicSenderIsNotRedirectedByWhatItAnnounces()
    {
        var bus = new FakeLanBus();
        var listener = bus.CreateNode("192.168.1.10");
        using var discovery = NewDiscovery(listener, new FakeTimeProvider(), "me", announce: false);
        await discovery.StartAsync(TestContext.Current.CancellationToken);

        listener.DeliverDatagram(new DiscoveryDatagram(Announce("remote", "192.168.1.99"), IPAddress.Parse("203.0.113.5")));

        discovery.PeersOf(Service).ShouldHaveSingleItem().EndPoint.Address.ToString().ShouldBe("203.0.113.5");
    }

    [Fact]
    public async Task AnAnnouncingNodeNamesItsDefaultRouteAddress_AndNoneWhenItHasNone()
    {
        var bus = new FakeLanBus();
        var withRoute = bus.CreateNode("192.168.1.10");
        var withoutRoute = bus.CreateNode("192.168.1.11");
        using var a = NewDiscovery(withRoute, new FakeTimeProvider(), "a", announce: true, address: "192.168.1.10");
        using var b = NewDiscovery(withoutRoute, new FakeTimeProvider(), "b", announce: true, address: null);
        await a.StartAsync(TestContext.Current.CancellationToken);
        await b.StartAsync(TestContext.Current.CancellationToken);

        LanProtocol.Parse(withRoute.Broadcasts.First()).Properties![LanProtocol.PropAddress].ShouldBe("192.168.1.10");
        LanProtocol.Parse(withoutRoute.Broadcasts.First()).Properties!.ContainsKey(LanProtocol.PropAddress).ShouldBeFalse();
    }
}
