using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using LAN.Lib;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

/// <summary>
/// The real neighbour table of the machine running the tests, which no other test reads. <b>Skipped unless the environment
/// variable <see cref="EnableVariable"/> is <c>1</c></b>: it needs a network with a default gateway and reads this
/// machine's own table (and, on Windows, may send one ARP request), so it never runs in CI or in a plane. Run it by hand:
/// <code>
/// LANLIB_LIVE_NEIGHBOURS=1 dotnet test src/LAN.Lib.Tests -c Release --filter "FullyQualifiedName~LiveNeighbourTests"
/// </code>
/// (PowerShell: <c>$env:LANLIB_LIVE_NEIGHBOURS = '1'</c> first.)
/// </summary>
public class LiveNeighbourTests
{
    /// <summary>The environment variable that turns the live test on.</summary>
    public const string EnableVariable = "LANLIB_LIVE_NEIGHBOURS";

    private static bool Enabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

    [Fact]
    public async Task TheDefaultGatewayHasAMacInTheRealNeighbourTable()
    {
        Assert.SkipUnless(Enabled, $"Reads this machine's own neighbour table: set {EnableVariable}=1 to run it.");
        var gateway = DefaultGateway();
        Assert.SkipWhen(gateway is null, "This machine has no IPv4 default gateway to look up.");

        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"Default gateway: {gateway}");

        // Talk to it first, as an app does before it asks: sending is what makes the machine learn its MAC, and the table
        // holds only what the machine learnt. (Port 9 is "discard": nobody needs the datagram, only the neighbour entry.)
        using (var udp = new UdpClient(AddressFamily.InterNetwork))
        {
            await udp.SendAsync(new byte[1], new IPEndPoint(gateway!, 9), TestContext.Current.CancellationToken);
        }

        // The row is learnt a moment after the send when it was not held already, so ask a few times before giving up.
        var lookup = new LanNeighbours();
        PhysicalAddress? mac = null;
        for (var attempt = 0; attempt < 10 && mac is null; attempt++)
        {
            mac = await lookup.PhysicalAddressOfAsync(gateway!, TestContext.Current.CancellationToken);
            if (mac is null)
            {
                await Task.Delay(200, TestContext.Current.CancellationToken);
            }
        }

        var table = await LanNeighbourTables.ForThisMachine().FindAsync(gateway!, TestContext.Current.CancellationToken);
        output?.WriteLine($"Table row: {(table is null ? "none" : $"{table.PhysicalAddress} ({table.State}, interface {table.Interface ?? "-"})")}");
        output?.WriteLine($"LanNeighbours: {(mac is null ? "null" : mac.ToString())}");
        mac.ShouldNotBeNull($"the default gateway {gateway} is on this machine's link, so its MAC is in the table, or answers an ARP request");
        mac.GetAddressBytes().Length.ShouldBe(6);
        mac.GetAddressBytes().Any(b => b != 0).ShouldBeTrue();
    }

    /// <summary>
    /// The IPv4 default gateway of the adapter this machine's default route leaves by, or null. Taken from
    /// <see cref="NetworkInterface"/>, not from the code under test.
    /// </summary>
    private static IPAddress? DefaultGateway()
    {
        var route = LanAddress.DefaultRoute();
        IPAddress? any = null;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }
            var properties = adapter.GetIPProperties();
            var gateway = properties.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            if (gateway is null)
            {
                continue;
            }
            if (route is not null && properties.UnicastAddresses.Any(u => u.Address.Equals(route)))
            {
                return gateway;
            }
            any ??= gateway;
        }
        return any;
    }
}
