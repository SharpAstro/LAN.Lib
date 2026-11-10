using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace LAN.Lib;

/// <summary>
/// <see cref="ILanNeighbourTable"/> over Windows' own neighbour table: <c>GetIpNetTable2</c> in <c>iphlpapi.dll</c>, the
/// ARP cache for IPv4 and the NDP cache for IPv6, through source-generated <c>LibraryImport</c> calls. Windows only
/// (<see cref="SupportedOSPlatformAttribute"/> on the public constructor), reached through
/// <see cref="LanNeighbourTables.ForThisMachine"/> on other platforms as the table that holds nothing.
/// <para><b>The table is asked for the one address family of the address looked up</b>, and the row is picked by
/// <see cref="WindowsNeighbourRows"/>. A row's interface is the interface index as text, since this table names an
/// adapter by index. A row whose physical address is not six bytes has none (a link without a MAC), never a wrong one.</para>
/// <para><b>By default it only reports what the machine already learnt.</b> Pass <c>fillMissing: true</c> and an IPv4
/// address with no usable row is asked for on the link (<c>SendARP</c>), which finds a peer that was never talked to over
/// IP (one found by a broadcast, say). The answer is <see cref="LanNeighbourState.Reachable"/>, as the host just answered.
/// <b>It is off by default because a router can answer for a host that is not there.</b> A router doing proxy ARP replies
/// to a request for ANY address on its subnet with its own MAC, so on such a network a fill for a peer that has gone gives
/// the router's MAC, which names no device and is the wrong answer this library exists not to give (measured on a home
/// network: an unused address came back with the gateway's MAC in 1.2 seconds). Fill only where there is no such router, as
/// on a device's own access-point network (a WiFi dongle at 192.168.4.1), and know that a MAC found this way equal to the
/// gateway's is the router's.</para>
/// <para><c>SendARP</c> cannot be cancelled and takes seconds when nobody answers, so a fill runs on a pool thread and the
/// caller waits for it only as long as its token lets it (<see cref="LanNeighbours"/> gives it
/// <see cref="LanNeighbours.LookupBudget"/>); the call then ends on its own. At most <see cref="MaxFillsInFlight"/> run at
/// once, and a lookup that finds the room taken does without the fill, so a client that keeps asking about a host that
/// never answers cannot pile threads up.</para>
/// <para>The table is read on a pool thread as well, so a native call that blocked would hold that thread and not the
/// caller's.</para>
/// </summary>
public sealed class WindowsNeighbourTable : ILanNeighbourTable
{
    /// <summary>How many <c>SendARP</c> fills may be waiting for an answer at once, per table.</summary>
    public const int MaxFillsInFlight = 2;

    // MIB_IPNET_TABLE2 is a ULONG count and then the rows, which start on the 8-byte boundary the rows' LUID needs.
    private const int TableHeaderLength = 8;

    private readonly Func<IPAddress, LanNeighbourEntry?> _read;
    private readonly Func<IPAddress, PhysicalAddress?> _ask;
    private readonly bool _fillMissing;
    private int _fillsInFlight;

    /// <summary>The neighbour table of this Windows machine.</summary>
    /// <param name="fillMissing">
    /// True to ask the link for an IPv4 address the table has no usable row for; false (the default) for a table that
    /// never puts a packet on the wire. See the class remarks for why that is not on by default.
    /// </param>
    [SupportedOSPlatform("windows")]
    public WindowsNeighbourTable(bool fillMissing = false) : this(ReadTable, AskLink, fillMissing)
    {
    }

    /// <summary>
    /// A table over <paramref name="read"/> (the row for an address, or null) and <paramref name="ask"/> (the MAC the
    /// link gives for an IPv4 address, or null), which a test answers and which block like the native calls do.
    /// </summary>
    internal WindowsNeighbourTable(Func<IPAddress, LanNeighbourEntry?> read, Func<IPAddress, PhysicalAddress?> ask, bool fillMissing)
    {
        _read = read;
        _ask = ask;
        _fillMissing = fillMissing;
    }

    /// <summary>True when this table asks the link for an address it has no usable row for.</summary>
    internal bool FillsMissing => _fillMissing;

    /// <summary>How many fills are waiting for an answer now: those a caller gave up on count until the call returns.</summary>
    internal int FillsInFlight => Volatile.Read(ref _fillsInFlight);

    /// <summary>Run after each fill has given its slot back, so a test can await the moment instead of polling for it.</summary>
    internal Action? FillReturned { get; init; }

    /// <inheritdoc/>
    public async Task<LanNeighbourEntry?> FindAsync(IPAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return null;
        }

        // Off the caller's thread: it returns this task at once, so a native call that blocked cannot hold the caller.
        var found = await Task.Run(() => _read(address), cancellationToken).ConfigureAwait(false);
        if (found is { IsUsable: true } || !_fillMissing || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return found;
        }

        var mac = await Fill(address).WaitAsync(cancellationToken).ConfigureAwait(false);
        return mac is null ? found : new LanNeighbourEntry(address, mac, LanNeighbourState.Reachable, found?.Interface);
    }

    private Task<PhysicalAddress?> Fill(IPAddress address)
    {
        if (Interlocked.Increment(ref _fillsInFlight) > MaxFillsInFlight)
        {
            Interlocked.Decrement(ref _fillsInFlight);
            return Task.FromResult<PhysicalAddress?>(null);
        }

        // The slot is given back when the native call returns, not when the caller stops waiting for it.
        return Task.Run(() =>
        {
            try
            {
                return _ask(address);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // No IP Helper to ask (a stripped-down Windows): no answer, as for a host that does not reply.
                return null;
            }
            finally
            {
                Interlocked.Decrement(ref _fillsInFlight);
                FillReturned?.Invoke();
            }
        });
    }

    [SupportedOSPlatform("windows")]
    private static unsafe LanNeighbourEntry? ReadTable(IPAddress address)
    {
        var family = address.AddressFamily == AddressFamily.InterNetwork ? WindowsNeighbourRows.AfInet : WindowsNeighbourRows.AfInet6;
        if (IpHelperNative.GetIpNetTable2(family, out var table) != 0 || table == 0)
        {
            // NETIO_STATUS other than NO_ERROR: there is no table to free, and nothing to say about the address.
            return null;
        }
        try
        {
            var count = *(uint*)table;
            var rows = new ReadOnlySpan<MibIpNetRow2>((byte*)table + TableHeaderLength, (int)Math.Min(count, int.MaxValue));
            return WindowsNeighbourRows.Find(rows, address);
        }
        finally
        {
            IpHelperNative.FreeMibTable(table);
        }
    }

    [SupportedOSPlatform("windows")]
    private static PhysicalAddress? AskLink(IPAddress ipv4)
    {
        // 8 bytes, not 6: SendARP is given room for a ULONG pair and says how much of it is the MAC.
        Span<byte> mac = stackalloc byte[8];
        var length = (uint)mac.Length;
        var status = IpHelperNative.SendARP(WindowsNeighbourRows.ToIpAddr(ipv4), 0, ref MemoryMarshal.GetReference(mac), ref length);
        return WindowsNeighbourRows.ToPhysicalAddress(status, mac, length);
    }
}
