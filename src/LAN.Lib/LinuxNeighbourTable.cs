using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LAN.Lib;

/// <summary>
/// <see cref="ILanNeighbourTable"/> over Linux's <c>/proc/net/arp</c>: the kernel's IPv4 ARP cache as text, one row per
/// neighbour. <b>IPv4 only</b>: the IPv6 neighbour cache is not in a file, only in netlink or the <c>ip neigh</c> tool, so
/// an IPv6 address is not found here.
/// <para>The text is read afresh on every call (a few lines, and the table changes as the machine talks), and a file that
/// cannot be read, a container without <c>/proc/net</c>, say, is an empty table and not an exception.</para>
/// </summary>
public sealed class LinuxNeighbourTable : ILanNeighbourTable
{
    /// <summary>Where the kernel publishes its ARP cache.</summary>
    public const string DefaultPath = "/proc/net/arp";

    private const int FlagComplete = 0x2;
    private const int FlagPermanent = 0x4;
    private const int EthernetAddressLength = 6;

    private readonly string _path;

    /// <summary>The table at <see cref="DefaultPath"/>.</summary>
    public LinuxNeighbourTable() : this(DefaultPath)
    {
    }

    /// <summary>The table in <paramref name="path"/>, for a copy of the file in another place.</summary>
    public LinuxNeighbourTable(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    /// <inheritdoc/>
    public async Task<LanNeighbourEntry?> FindAsync(IPAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Pick(ParseProcNetArp(text), address);
    }

    /// <summary>
    /// The row to answer for <paramref name="address"/>: when the same address is held on several adapters (two links
    /// that both use 192.168.4.x), one that names a host before one that does not, and otherwise the first.
    /// </summary>
    internal static LanNeighbourEntry? Pick(IReadOnlyList<LanNeighbourEntry> entries, IPAddress address)
    {
        LanNeighbourEntry? first = null;
        foreach (var entry in entries)
        {
            if (!entry.Address.Equals(address))
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
    /// The rows in the text of <c>/proc/net/arp</c>, which looks like this:
    /// <code>
    /// IP address       HW type     Flags       HW address            Mask     Device
    /// 192.168.1.1      0x1         0x2         aa:bb:cc:dd:ee:ff     *        eth0
    /// 192.168.1.9      0x1         0x0         00:00:00:00:00:00     *        eth0
    /// </code>
    /// The header is skipped, as is any line that is not a whole row: a dotted IPv4 address, a hex type, a hex flags
    /// field, a six-octet colon-separated hardware address, a mask and a device. A flags field without the complete bit
    /// (<c>0x2</c>) is <see cref="LanNeighbourState.Incomplete"/>, one with the permanent bit (<c>0x4</c>) is
    /// <see cref="LanNeighbourState.Permanent"/>, and any other complete row is <see cref="LanNeighbourState.Complete"/>,
    /// since this table does not say how fresh a row is. Pure: no file, no clock.
    /// </summary>
    public static IReadOnlyList<LanNeighbourEntry> ParseProcNetArp(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var entries = new List<LanNeighbourEntry>();
        foreach (var line in text.Split('\n'))
        {
            if (TryParseRow(line, out var entry))
            {
                entries.Add(entry);
            }
        }
        return entries;
    }

    private static bool TryParseRow(string line, out LanNeighbourEntry entry)
    {
        entry = null!;
        // Columns are padded with spaces; fewer than six is a header remnant or a truncated read.
        var fields = line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 6
            || !TryParseDottedIPv4(fields[0], out var address)
            || !TryParseHex(fields[1], out _)
            || !TryParseHex(fields[2], out var flags)
            || !TryParseColonSeparatedAddress(fields[3], out var physical))
        {
            return false;
        }

        var state = (flags & FlagComplete) == 0 ? LanNeighbourState.Incomplete
            : (flags & FlagPermanent) != 0 ? LanNeighbourState.Permanent
            : LanNeighbourState.Complete;
        entry = new LanNeighbourEntry(address, physical, state, fields[5]);
        return true;
    }

    // IPAddress.TryParse takes "1" as 0.0.0.1 and "192.168.1" as 192.168.0.1, which a kernel never prints; a row that
    // says so is damaged, not an address.
    private static bool TryParseDottedIPv4(string text, out IPAddress address)
    {
        if (text.AsSpan().Count('.') == 3 && IPAddress.TryParse(text, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            address = parsed;
            return true;
        }
        address = IPAddress.None;
        return false;
    }

    private static bool TryParseHex(string text, out int value)
    {
        var digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.AsSpan(2) : text.AsSpan();
        return int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    // PhysicalAddress.TryParse would take a hyphenated one, or none at all, and any length; the kernel prints six
    // colon-separated octets for the Ethernet and WiFi links this exists for, and the other hardware types are not a MAC.
    private static bool TryParseColonSeparatedAddress(string text, out PhysicalAddress address)
    {
        address = PhysicalAddress.None;
        var parts = text.Split(':');
        if (parts.Length != EthernetAddressLength)
        {
            return false;
        }
        var bytes = new byte[EthernetAddressLength];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (parts[i].Length != 2 || !byte.TryParse(parts[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return false;
            }
        }
        address = new PhysicalAddress(bytes);
        return true;
    }
}
