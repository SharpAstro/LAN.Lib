using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LAN.Lib;

/// <summary>
/// The MAC address behind an IP address on this link, from the operating system's neighbour table. A WiFi device is named
/// by an address that names nothing (a Sky-Watcher dongle is 192.168.4.1 whichever mount it is in, and a DHCP address
/// moves), whereas its radio's MAC is the same either way, and the machine learnt it the moment it talked to the device.
/// <para><b>What it answers is the host's own address or none.</b> Null for an address that cannot have a host on this
/// link: loopback, unspecified, multicast and broadcast, and any address outside every subnet this machine is attached to
/// (<see cref="ILanLinks"/>), whose neighbour entry would be the router's and would name the wrong device. Null as well for
/// a row that holds no address yet (incomplete or unreachable) and for an all-zero or all-ones one
/// (<see cref="LanNeighbourEntry.IsUsable"/>). An IPv4 address seen through an IPv6 socket
/// (<c>::ffff:192.168.4.1</c>) is the IPv4 address.</para>
/// <para><b>Not cached</b>, deliberately: the table is already the machine's cache, and the same address can name another
/// device a minute later (that dongle's 192.168.4.1 after the user joins the next mount's network), so an answer of ours
/// would outlive its truth. Ask right after talking to the device, which is when the table has it.</para>
/// <para><b>Bounded</b> by <see cref="LookupBudget"/> on its own clock: a table that never answers gives null rather than
/// holding the caller, even one that ignores the token it is given. A caller that gives up first gets an
/// <see cref="OperationCanceledException"/> as usual.</para>
/// </summary>
public sealed class LanNeighbours(ILanNeighbourTable table, ILanLinks links, TimeProvider timeProvider)
{
    /// <summary>How long the table may take before the address counts as having no MAC.</summary>
    public static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(2);

    /// <summary>The lookup over this machine's own neighbour table and adapters, on the system clock.</summary>
    public LanNeighbours() : this(LanNeighbourTables.ForThisMachine(), new NetworkInterfaceLinks(), TimeProvider.System)
    {
    }

    /// <summary>The MAC address of the host behind <paramref name="address"/> on this link, or null when there is none.</summary>
    public async Task<PhysicalAddress?> PhysicalAddressOfAsync(IPAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        cancellationToken.ThrowIfCancellationRequested();

        var key = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (!CouldHaveAHostOnALink(key) || !links.IsOnLink(key))
        {
            return null;
        }

        // The caller's token ends the wait as well as the budget, but only the budget turns into "no MAC".
        using var budget = new CancellationTokenSource(LookupBudget, timeProvider);
        using var callerGaveUp = cancellationToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), budget);
        try
        {
            var lookup = table.FindAsync(key, budget.Token);
            // A table that gives up late (or never) still leaves an exception to be observed, not to be lost.
            _ = lookup.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            // WaitAsync, so the budget holds even for a table that ignores its token.
            var entry = await lookup.WaitAsync(budget.Token).ConfigureAwait(false);
            return entry is { IsUsable: true } ? entry.PhysicalAddress : null;
        }
        catch (OperationCanceledException)
        {
            // A caller that gave up gets the exception of its own token, not the budget's; otherwise it was the budget,
            // or a table that cancelled itself, and either is no MAC.
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A table that fails has no answer; the caller asked about a device, not about the table.
            return null;
        }
    }

    /// <summary>
    /// False for the addresses no host can answer for on a link: unspecified, loopback, multicast, and (IPv4) the limited
    /// broadcast and the reserved block above multicast.
    /// </summary>
    private static bool CouldHaveAHostOnALink(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }
        return address.AddressFamily switch
        {
            // 224.0.0.0/4 is multicast and 240.0.0.0/4 is reserved, 255.255.255.255 the limited broadcast among it.
            AddressFamily.InterNetwork => address.GetAddressBytes()[0] < 224,
            AddressFamily.InterNetworkV6 => !address.IsIPv6Multicast,
            _ => false,
        };
    }
}
