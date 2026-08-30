using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net;
using LAN.Lib;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

/// <summary>
/// The address arithmetic behind per-interface announcing. Discovery used to send ONE datagram to
/// 255.255.255.255, which leaves a multi-homed host on exactly one interface — whichever wins the
/// route — while receiving (bound to Any) hears them all. That asymmetry made a host see its peers but
/// stay invisible to them. Announcing per interface needs each one's directed broadcast, and getting
/// the octet order wrong there fails silently: the datagram goes somewhere plausible and nobody
/// answers.
/// </summary>
public class UdpLanTransportTests
{
    [Theory]
    [InlineData("192.168.0.70", "255.255.255.0", "192.168.0.255")]      // /24, the ordinary LAN
    [InlineData("172.27.11.241", "255.255.240.0", "172.27.15.255")]     // /20, mask spans an octet
    [InlineData("10.1.2.3", "255.0.0.0", "10.255.255.255")]             // /8
    [InlineData("192.168.240.1", "255.255.255.0", "192.168.240.255")]   // a Hyper-V virtual switch
    [InlineData("169.254.43.92", "255.255.0.0", "169.254.255.255")]     // /16 link-local
    public void DirectedBroadcast_SetsTheHostBits(string address, string mask, string expected)
    {
        var directed = UdpLanTransport.DirectedBroadcast(IPAddress.Parse(address), IPAddress.Parse(mask));

        directed.ShouldNotBeNull();
        // Asserted as a STRING: the octet order is the whole risk here, and comparing IPAddress
        // objects built the same wrong way round would agree with itself.
        directed.ToString().ShouldBe(expected);
    }

    [Fact]
    public async Task WhenTheDiscoveryPortCannotBeBound_ItDegradesToAnnounceOnlyInsteadOfThrowing()
    {
        // The failure that took a whole GUI down at DI resolution: a port the OS will not let us bind
        // (Windows answers WSAEACCES for a port inside a Hyper-V / WSL exclusion). Simulated with an
        // EXCLUSIVE holder of the port -- the same class of failure, portable to Linux (EADDRINUSE).
        // Held on the WILDCARD address: Windows lets a wildcard bind coexist with an exclusive bind on a
        // specific address, so a loopback holder would not block the transport's 0.0.0.0 bind at all.
        using var holder = new UdpClient();
        holder.ExclusiveAddressUse = true;
        holder.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)holder.Client.LocalEndPoint!).Port;

        await using var transport = new UdpLanTransport(port);

        transport.ReceiveFailure.ShouldNotBeNull();
        transport.DiscoveryPort.ShouldBe(port);
        transport.Degradation.ShouldNotBeNull().ShouldContain(port.ToString());
        // Announcing still works: an unbound socket sends from an ephemeral port the OS picks.
        await transport.BroadcastAsync("SALAN 1 BYE probe", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AListeningTransportReportsNoDegradation()
    {
        await using var transport = new UdpLanTransport(0); // any free port: this test is about the healthy path
        transport.ReceiveFailure.ShouldBeNull();
        transport.Degradation.ShouldBeNull();
    }

    /// <summary>A /32 is a point-to-point or VPN adapter. Its "directed broadcast" is its own address,
    /// so announcing there would only ever reach ourselves — the caller skips it rather than burning a
    /// datagram per beacon.</summary>
    [Fact]
    public void DirectedBroadcast_SlashThirtyTwo_IsNull()
    {
        UdpLanTransport.DirectedBroadcast(IPAddress.Parse("10.8.0.6"), IPAddress.Parse("255.255.255.255"))
            .ShouldBeNull();
    }

    [Fact]
    public void DirectedBroadcast_IPv6_IsNull()
    {
        UdpLanTransport.DirectedBroadcast(IPAddress.Parse("fe80::1"), IPAddress.Parse("255.255.255.0"))
            .ShouldBeNull();
    }
}
