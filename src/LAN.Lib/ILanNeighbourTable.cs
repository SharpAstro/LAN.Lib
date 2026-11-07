using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace LAN.Lib;

/// <summary>
/// How fresh a neighbour table entry is, as the operating system's own tables say it. The states of Windows'
/// <c>NL_NEIGHBOR_STATE</c> and of Linux's neighbour cache are the same ones under different spellings, so this is their
/// common set; a table that cannot tell them apart answers <see cref="Complete"/>.
/// </summary>
public enum LanNeighbourState
{
    /// <summary>The address is being resolved, or was asked for and nobody answered: no address to use.</summary>
    Incomplete = 0,

    /// <summary>The host did not answer a probe (Linux <c>FAILED</c>, Windows <c>Unreachable</c>): no address to use.</summary>
    Unreachable,

    /// <summary>The address is known, and being confirmed by a probe.</summary>
    Probe,

    /// <summary>The address is known, and a probe is about to confirm it.</summary>
    Delay,

    /// <summary>The address is known but not confirmed lately. It is still the host's address.</summary>
    Stale,

    /// <summary>The host answered recently.</summary>
    Reachable,

    /// <summary>A static entry, written by an administrator rather than learnt.</summary>
    Permanent,

    /// <summary>
    /// A complete entry from a table that does not say how fresh it is (Linux's <c>/proc/net/arp</c> has only a "complete"
    /// flag), so neither reachable nor stale is a claim it can make.
    /// </summary>
    Complete,
}

/// <summary>One row of the operating system's neighbour (ARP / NDP) table: who answers for an address on this link.</summary>
/// <param name="Address">The IP address the row is about.</param>
/// <param name="PhysicalAddress">
/// The link-layer (MAC) address the row holds. <see cref="System.Net.NetworkInformation.PhysicalAddress.None"/>, or all zero,
/// for a row that has none yet.
/// </param>
/// <param name="State">How fresh the row is.</param>
/// <param name="Interface">
/// The adapter the row belongs to, as the table names it (<c>eth0</c>, <c>wlan0</c>), or null when it does not say.
/// </param>
public sealed record LanNeighbourEntry(IPAddress Address, PhysicalAddress PhysicalAddress, LanNeighbourState State, string? Interface = null)
{
    /// <summary>
    /// True when the row names a host: a <see cref="State"/> that holds an address, and an address that is neither all
    /// zero (a row still being resolved) nor all ones (the broadcast address, which a table can hold for a directed
    /// broadcast and which belongs to no host).
    /// </summary>
    public bool IsUsable => State switch
    {
        LanNeighbourState.Probe or LanNeighbourState.Delay or LanNeighbourState.Stale
            or LanNeighbourState.Reachable or LanNeighbourState.Permanent or LanNeighbourState.Complete => HasHostAddress(PhysicalAddress),
        _ => false,
    };

    private static bool HasHostAddress(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 0)
        {
            return false;
        }
        var zero = true;
        var ones = true;
        foreach (var b in bytes)
        {
            zero &= b == 0x00;
            ones &= b == 0xFF;
        }
        return !zero && !ones;
    }
}

/// <summary>
/// The operating system's neighbour table (the ARP cache for IPv4, the NDP cache for IPv6), so a test can answer it and
/// nothing in a test run reads the machine's own table. <see cref="LanNeighbours"/> is the lookup built on it.
/// </summary>
public interface ILanNeighbourTable
{
    /// <summary>
    /// The table's row for exactly <paramref name="address"/>, or null when it holds none (or this table cannot see the
    /// kind of address it is). Not a lookup on the wire in itself: a table reports what the machine already learnt, so it
    /// is cheap, with one declared exception that is off unless asked for (<see cref="WindowsNeighbourTable"/> built with
    /// <c>fillMissing: true</c> asks the link for an IPv4 address it has no row for, for as long as
    /// <paramref name="cancellationToken"/> lets the caller wait). A row that is not
    /// <see cref="LanNeighbourEntry.IsUsable"/> may be returned as it is; the caller decides what to do with it.
    /// </summary>
    Task<LanNeighbourEntry?> FindAsync(IPAddress address, CancellationToken cancellationToken);
}

/// <summary>The neighbour table of the machine this runs on.</summary>
public static class LanNeighbourTables
{
    /// <summary>A table that holds nothing, for a platform whose table is not read.</summary>
    public static ILanNeighbourTable Empty { get; } = new EmptyNeighbourTable();

    /// <summary>
    /// The table of this machine: Windows' <c>GetIpNetTable2</c> (<see cref="WindowsNeighbourTable"/>, IPv4 and IPv6, read
    /// only: build one with <c>fillMissing: true</c> to also ask the link, see its remarks), or Linux's
    /// <c>/proc/net/arp</c> (<see cref="LinuxNeighbourTable"/>, IPv4 only). On any other platform it is <see cref="Empty"/>,
    /// which gives <see cref="LanNeighbours"/> no address rather than a wrong one.
    /// <para><b>macOS is one of those, deliberately.</b> Its table is not a file and not a library call: it is the routing
    /// socket's dump, <c>sysctl</c> with <c>NET_RT_FLAGS</c> and <c>RTF_LLINFO</c>, a stream of variable-length
    /// <c>rt_msghdr</c> records each followed by <c>sockaddr</c>s rounded up to a platform-specific alignment, whose
    /// <c>sockaddr_dl</c> carries the MAC after an interface name of any length. That layout cannot be checked from the
    /// machines this library is built and tested on, and a parse that is off by a byte answers with another device's MAC,
    /// which is worse than none. So it answers null until it is written and run on a Mac (or <c>arp -an</c> is read by an
    /// app that wants it now, behind its own <see cref="ILanNeighbourTable"/>).</para>
    /// </summary>
    public static ILanNeighbourTable ForThisMachine()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsNeighbourTable();
        }
        if (OperatingSystem.IsLinux())
        {
            return new LinuxNeighbourTable();
        }
        // macOS and every other platform: see the remarks above.
        return Empty;
    }

    private sealed class EmptyNeighbourTable : ILanNeighbourTable
    {
        public Task<LanNeighbourEntry?> FindAsync(IPAddress address, CancellationToken cancellationToken) =>
            Task.FromResult<LanNeighbourEntry?>(null);
    }
}
