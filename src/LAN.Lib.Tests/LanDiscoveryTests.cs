using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using LAN.Lib;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

public class LanDiscoveryTests
{
    private const string LocalId = "local-peer";
    private const string Service = "tianwen-server";

    private static LanDiscovery NewDiscovery(
        FakeLanTransport transport, FakeTimeProvider time,
        string peerId = LocalId, string service = Service, bool announce = true, string nodeId = "")
    {
        var options = new LanDiscoveryOptions
        {
            ServiceName = service,
            ServicePort = 1888,
            NodeName = "Me",
            Announce = announce,
        };
        return new LanDiscovery(transport, time, options, new LanIdentity(peerId, nodeId));
    }

    private static string RemoteAnnounce(
        string peerId, int port, string name, string service = Service,
        string nodeId = "", string machine = "", int pid = 0)
    {
        var props = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(nodeId)) props[LanProtocol.PropNodeId] = nodeId;
        if (!string.IsNullOrEmpty(machine)) props[LanProtocol.PropMachineName] = machine;
        if (pid != 0) props[LanProtocol.PropPid] = pid.ToString();
        return LanProtocol.EncodeAnnounce(peerId, service, port, name, props);
    }

    [Fact]
    public async Task RemoteAnnounce_Appears_WithSenderAddressAndAnnouncedPort()
    {
        var bus = new FakeLanBus();
        var time = new FakeTimeProvider();
        var local = bus.CreateNode("192.168.1.10");
        using var discovery = NewDiscovery(local, time);
        await discovery.StartAsync(TestContext.Current.CancellationToken);

        local.DeliverDatagram(new DiscoveryDatagram(
            RemoteAnnounce("remote-1", 1888, "Rig", nodeId: "node-xyz", machine: "MiniPC", pid: 4321),
            IPAddress.Parse("192.168.1.20")));

        var peers = discovery.Peers;
        peers.Count.ShouldBe(1);
        peers[0].PeerId.ShouldBe("remote-1");
        peers[0].Service.ShouldBe(Service);
        peers[0].Name.ShouldBe("Rig");
        peers[0].NodeId.ShouldBe("node-xyz");
        peers[0].MachineName.ShouldBe("MiniPC");
        peers[0].Pid.ShouldBe(4321);
        // Host comes from the datagram's sender; port comes from the announce payload.
        peers[0].EndPoint.ShouldBe(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 1888));
    }

    [Fact]
    public async Task OwnAnnounce_IsIgnored()
    {
        var bus = new FakeLanBus();
        var time = new FakeTimeProvider();
        var local = bus.CreateNode("192.168.1.10");
        using var discovery = NewDiscovery(local, time);

        // StartAsync() broadcasts our own announce, which the bus echoes back to us like real UDP does.
        await discovery.StartAsync(TestContext.Current.CancellationToken);

        discovery.Peers.ShouldBeEmpty();
    }

    [Fact]
    public async Task Bye_RemovesPeer()
    {
        var bus = new FakeLanBus();
        var time = new FakeTimeProvider();
        var local = bus.CreateNode("192.168.1.10");
        using var discovery = NewDiscovery(local, time);
        await discovery.StartAsync(TestContext.Current.CancellationToken);

        local.DeliverDatagram(new DiscoveryDatagram(
            RemoteAnnounce("remote-1", 1, "Rig"), IPAddress.Parse("192.168.1.20")));
        discovery.Peers.Count.ShouldBe(1);

        local.DeliverDatagram(new DiscoveryDatagram(
            LanProtocol.EncodeBye("remote-1"), IPAddress.Parse("192.168.1.20")));
        discovery.Peers.ShouldBeEmpty();
    }

    [Fact]
    public async Task StalePeer_ExpiresAfterTimeout()
    {
        var bus = new FakeLanBus();
        var time = new FakeTimeProvider();
        var local = bus.CreateNode("192.168.1.10");
        using var discovery = NewDiscovery(local, time);
        await discovery.StartAsync(TestContext.Current.CancellationToken);

        local.DeliverDatagram(new DiscoveryDatagram(
            RemoteAnnounce("remote-1", 1, "Rig"), IPAddress.Parse("192.168.1.20")));
        discovery.Peers.Count.ShouldBe(1);

        // Advance past the timeout; the beacon timer fires prune along the way.
        time.Advance(LanDiscovery.PeerTimeout + LanDiscovery.BeaconInterval);

        discovery.Peers.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReAnnounce_KeepsPeerAlive()
    {
        var bus = new FakeLanBus();
        var time = new FakeTimeProvider();
        var local = bus.CreateNode("192.168.1.10");
        using var discovery = NewDiscovery(local, time);
        await discovery.StartAsync(TestContext.Current.CancellationToken);

        // Refresh every 2s (< the 5s timeout) — the peer must never expire.
        for (var i = 0; i < 10; i++)
        {
            local.DeliverDatagram(new DiscoveryDatagram(
                RemoteAnnounce("remote-1", 1, "Rig"), IPAddress.Parse("192.168.1.20")));
            time.Advance(TimeSpan.FromSeconds(2));
        }

        discovery.Peers.Count.ShouldBe(1);
    }

    [Fact]
    public async Task PeersOf_FiltersByService()
    {
        var bus = new FakeLanBus();
        var time = new FakeTimeProvider();
        var local = bus.CreateNode("192.168.1.10");
        using var discovery = NewDiscovery(local, time);
        await discovery.StartAsync(TestContext.Current.CancellationToken);

        local.DeliverDatagram(new DiscoveryDatagram(
            RemoteAnnounce("srv-1", 1888, "Rig", service: "tianwen-server"), IPAddress.Parse("192.168.1.20")));
        local.DeliverDatagram(new DiscoveryDatagram(
            RemoteAnnounce("gui-1", 5000, "Laptop", service: "tianwen-gui"), IPAddress.Parse("192.168.1.21")));

        discovery.Peers.Count.ShouldBe(2);

        var servers = discovery.PeersOf("tianwen-server");
        servers.Count.ShouldBe(1);
        servers[0].PeerId.ShouldBe("srv-1");

        // Filtering is case-insensitive.
        discovery.PeersOf("TIANWEN-GUI").Count.ShouldBe(1);
        discovery.PeersOf("nobody").ShouldBeEmpty();
    }

    [Fact]
    public async Task Changed_FiresOnAddAndRemove_NotOnRefresh()
    {
        var bus = new FakeLanBus();
        var time = new FakeTimeProvider();
        var local = bus.CreateNode("192.168.1.10");
        using var discovery = NewDiscovery(local, time);
        var changes = 0;
        discovery.Changed += () => changes++;
        await discovery.StartAsync(TestContext.Current.CancellationToken);

        var announce = RemoteAnnounce("remote-1", 1, "Rig");
        var from = IPAddress.Parse("192.168.1.20");

        local.DeliverDatagram(new DiscoveryDatagram(announce, from));
        changes.ShouldBe(1); // added

        local.DeliverDatagram(new DiscoveryDatagram(announce, from));
        changes.ShouldBe(1); // refresh of a known peer — no change

        local.DeliverDatagram(new DiscoveryDatagram(LanProtocol.EncodeBye("remote-1"), from));
        changes.ShouldBe(2); // removed
    }

    [Fact]
    public void NodeId_ExposesOwnIdentity()
    {
        var bus = new FakeLanBus();
        var time = new FakeTimeProvider();
        var local = bus.CreateNode("192.168.1.10");
        using var discovery = NewDiscovery(local, time, nodeId: "node-self");

        discovery.NodeId.ShouldBe("node-self");
    }

    [Fact]
    public void NodeId_EmptyForListenOnlyConsumer()
    {
        var bus = new FakeLanBus();
        var time = new FakeTimeProvider();
        var local = bus.CreateNode("192.168.1.10");
        using var discovery = NewDiscovery(local, time, announce: false);

        discovery.NodeId.ShouldBe("");
    }

    [Fact]
    public async Task ListenOnly_DoesNotBroadcast_ButStillReceives()
    {
        var bus = new FakeLanBus();
        var time = new FakeTimeProvider();
        var local = bus.CreateNode("192.168.1.10");
        using var discovery = NewDiscovery(local, time, announce: false);
        await discovery.StartAsync(TestContext.Current.CancellationToken);

        // A listen-only monitor never announces itself...
        local.Broadcasts.ShouldBeEmpty();

        // ...but still sees everyone else.
        local.DeliverDatagram(new DiscoveryDatagram(
            RemoteAnnounce("remote-1", 1, "Rig"), IPAddress.Parse("192.168.1.20")));
        discovery.Peers.Count.ShouldBe(1);
    }
}
