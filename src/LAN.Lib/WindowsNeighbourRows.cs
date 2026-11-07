using System;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LAN.Lib;

/// <summary>A <c>SOCKADDR_INET</c>: the 28 bytes of the larger of <c>SOCKADDR_IN</c> and <c>SOCKADDR_IN6</c>.</summary>
[InlineArray(WindowsNeighbourRows.SockAddrInetLength)]
internal struct SockAddrInet
{
    private byte _element0;
}

/// <summary>The <c>UCHAR PhysicalAddress[IF_MAX_PHYS_ADDRESS_LENGTH]</c> of a neighbour row.</summary>
[InlineArray(WindowsNeighbourRows.MaxPhysicalAddressLength)]
internal struct PhysicalAddressBuffer
{
    private byte _element0;
}

/// <summary>
/// A <c>MIB_IPNET_ROW2</c>, field for field, 88 bytes: what <c>GetIpNetTable2</c> hands back an array of. Plain
/// blittable data (no pointer, no string, no marshalling), so a row is read straight out of the table's memory and a
/// test builds one by hand. The padding the native struct has is the padding sequential layout gives it: the
/// interface LUID sits on an 8-byte boundary, and the reachability time follows the flags byte.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MibIpNetRow2
{
    /// <summary>The neighbour's IP address, as a <c>SOCKADDR_INET</c>: family at 0, then the IPv4 or the IPv6 form.</summary>
    public SockAddrInet Address;

    /// <summary>The interface the row was learnt on (<c>NET_IFINDEX</c>).</summary>
    public uint InterfaceIndex;

    /// <summary>The same interface as its LUID (<c>NET_LUID</c>), which does not change when the adapter is renumbered.</summary>
    public ulong InterfaceLuid;

    /// <summary>The link-layer address, of which only the first <see cref="PhysicalAddressLength"/> bytes count.</summary>
    public PhysicalAddressBuffer PhysicalAddress;

    /// <summary>How many bytes of <see cref="PhysicalAddress"/> are the address: 6 for Ethernet and WiFi, 0 for none yet.</summary>
    public uint PhysicalAddressLength;

    /// <summary>The <c>NL_NEIGHBOR_STATE</c>, see <see cref="WindowsNeighbourRows.ToState"/>.</summary>
    public uint State;

    /// <summary>The <c>IsRouter</c> and <c>IsUnreachable</c> bits.</summary>
    public byte Flags;

    /// <summary>Seconds since the neighbour last answered or last failed to (<c>LastReachable</c> or <c>LastUnreachable</c>).</summary>
    public uint ReachabilityTime;
}

/// <summary>
/// The pure half of <see cref="WindowsNeighbourTable"/>: turning the rows <c>GetIpNetTable2</c> returns into
/// <see cref="LanNeighbourEntry"/>s and picking the one for an address, and the two conversions <c>SendARP</c> needs.
/// Nothing here calls Windows or reads a clock, so it is tested on every platform from hand-built rows.
/// </summary>
internal static class WindowsNeighbourRows
{
    /// <summary><c>AF_INET</c>.</summary>
    public const ushort AfInet = 2;

    /// <summary><c>AF_INET6</c>.</summary>
    public const ushort AfInet6 = 23;

    /// <summary><c>sizeof(SOCKADDR_INET)</c>.</summary>
    public const int SockAddrInetLength = 28;

    /// <summary><c>IF_MAX_PHYS_ADDRESS_LENGTH</c>.</summary>
    public const int MaxPhysicalAddressLength = 32;

    /// <summary>The length of a MAC: Ethernet and WiFi, which is what a neighbour on a LAN has.</summary>
    public const int MacLength = 6;

    // SOCKADDR_IN: family (2), port (2), the address (4). SOCKADDR_IN6: family, port, flow info (4), the address (16),
    // the scope id (4). The family and the scope id are in host byte order, the address bytes as they go on the wire.
    private const int V4AddressOffset = 4;
    private const int V6AddressOffset = 8;
    private const int V6AddressLength = 16;
    private const int V6ScopeIdOffset = 24;

    /// <summary>
    /// The entry for one row, or false for a row this cannot read: an address family that is neither IPv4 nor IPv6. The
    /// physical address is the six bytes of a MAC, or <see cref="PhysicalAddress.None"/> for any other length (none yet,
    /// or a link that has no MAC), so a row never gives a MAC that is not one. The interface is the interface index as
    /// text, which is how this table names an adapter.
    /// </summary>
    public static bool TryToEntry(in MibIpNetRow2 row, out LanNeighbourEntry entry)
    {
        entry = null!;
        ReadOnlySpan<byte> sockaddr = row.Address;
        var family = MemoryMarshal.Read<ushort>(sockaddr);
        IPAddress address;
        if (family == AfInet)
        {
            address = new IPAddress(sockaddr.Slice(V4AddressOffset, 4));
        }
        else if (family == AfInet6)
        {
            address = new IPAddress(sockaddr.Slice(V6AddressOffset, V6AddressLength), MemoryMarshal.Read<uint>(sockaddr.Slice(V6ScopeIdOffset, 4)));
        }
        else
        {
            return false;
        }

        ReadOnlySpan<byte> mac = row.PhysicalAddress;
        var physical = row.PhysicalAddressLength == MacLength ? new PhysicalAddress(mac[..MacLength].ToArray()) : PhysicalAddress.None;
        entry = new LanNeighbourEntry(address, physical, ToState(row.State), row.InterfaceIndex.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>
    /// <c>NL_NEIGHBOR_STATE</c> as <see cref="LanNeighbourState"/>: 0 unreachable, 1 incomplete, 2 probe, 3 delay,
    /// 4 stale, 5 reachable, 6 permanent. A value outside those is a state this does not know, which is
    /// <see cref="LanNeighbourState.Incomplete"/> (no address to use) and never a usable one.
    /// </summary>
    public static LanNeighbourState ToState(uint state) => state switch
    {
        0 => LanNeighbourState.Unreachable,
        1 => LanNeighbourState.Incomplete,
        2 => LanNeighbourState.Probe,
        3 => LanNeighbourState.Delay,
        4 => LanNeighbourState.Stale,
        5 => LanNeighbourState.Reachable,
        6 => LanNeighbourState.Permanent,
        _ => LanNeighbourState.Incomplete,
    };

    /// <summary>
    /// The row to answer for <paramref name="address"/> among <paramref name="rows"/>: a row that names a host before one
    /// that does not (the same address can be held on two adapters, one of which never got an answer), and otherwise the
    /// first. Null when no row is about the address. Rows that cannot be read are skipped.
    /// </summary>
    public static LanNeighbourEntry? Find(ReadOnlySpan<MibIpNetRow2> rows, IPAddress address)
    {
        LanNeighbourEntry? first = null;
        foreach (ref readonly var row in rows)
        {
            if (!TryToEntry(in row, out var entry) || !SameHost(entry.Address, address))
            {
                continue;
            }
            if (entry.IsUsable)
            {
                return entry;
            }
            first ??= entry;
        }
        return first;
    }

    /// <summary>
    /// True when both are the same address of the same family. An IPv6 scope id says which link, so it is compared only
    /// when both carry one: an address read from a socket has the zone the row has, and one typed in has none.
    /// </summary>
    public static bool SameHost(IPAddress a, IPAddress b)
    {
        if (a.AddressFamily != b.AddressFamily)
        {
            return false;
        }
        Span<byte> x = stackalloc byte[V6AddressLength];
        Span<byte> y = stackalloc byte[V6AddressLength];
        if (!a.TryWriteBytes(x, out var xl) || !b.TryWriteBytes(y, out var yl) || !x[..xl].SequenceEqual(y[..yl]))
        {
            return false;
        }
        return a.AddressFamily != AddressFamily.InterNetworkV6 || a.ScopeId == 0 || b.ScopeId == 0 || a.ScopeId == b.ScopeId;
    }

    /// <summary>
    /// An IPv4 address as the <c>IPAddr</c> <c>SendARP</c> takes: the four bytes in the order they go on the wire, read as
    /// the machine's integer (so 192.168.4.1 is the bytes C0 A8 04 01 whichever way round the machine stores them).
    /// </summary>
    public static uint ToIpAddr(IPAddress ipv4)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (ipv4.AddressFamily != AddressFamily.InterNetwork || !ipv4.TryWriteBytes(bytes, out _))
        {
            throw new ArgumentException("An IPv4 address is needed.", nameof(ipv4));
        }
        return MemoryMarshal.Read<uint>(bytes);
    }

    /// <summary>
    /// The MAC <c>SendARP</c> filled in, or null: for a status other than <c>NO_ERROR</c> (nobody answered, the address is
    /// not on a link, the call is not supported), and for a length that is not the six bytes of a MAC.
    /// </summary>
    public static PhysicalAddress? ToPhysicalAddress(uint status, ReadOnlySpan<byte> buffer, uint length)
    {
        if (status != 0 || length != MacLength || buffer.Length < MacLength)
        {
            return null;
        }
        return new PhysicalAddress(buffer[..MacLength].ToArray());
    }
}
