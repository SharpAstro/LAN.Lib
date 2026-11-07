using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LAN.Lib;

/// <summary>
/// The links this machine is attached to, so a test can answer which addresses are on one of them and nothing in a test
/// run reads the machine's adapters. <see cref="LanNeighbours"/> asks it, since an address that is NOT on a link answers
/// from the router's neighbour entry, and the router's MAC names no device behind it.
/// </summary>
public interface ILanLinks
{
    /// <summary>True when <paramref name="address"/> lies inside the subnet of one of this machine's links.</summary>
    bool IsOnLink(IPAddress address);
}

/// <summary>
/// <see cref="ILanLinks"/> from the machine's network adapters: an address is on a link when it falls in the subnet
/// (unicast address and prefix length) of an adapter that is up. Loopback and tunnel adapters are not links.
/// <para>The adapters are read on every call, since a laptop changes network between one and the next, and a failure to
/// read them is "on no link", which gives <see cref="LanNeighbours"/> no address rather than a wrong one.</para>
/// </summary>
public sealed class NetworkInterfaceLinks : ILanLinks
{
    private readonly Func<IReadOnlyList<(IPAddress Address, int PrefixLength)>> _subnets;

    /// <summary>The links of this machine's adapters.</summary>
    public NetworkInterfaceLinks() : this(ReadAdapters)
    {
    }

    /// <summary>The links in <paramref name="subnets"/>, each an adapter's address and prefix length, for a test.</summary>
    internal NetworkInterfaceLinks(Func<IReadOnlyList<(IPAddress Address, int PrefixLength)>> subnets) => _subnets = subnets;

    /// <inheritdoc/>
    public bool IsOnLink(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        foreach (var (network, prefixLength) in _subnets())
        {
            if (InSubnet(address, network, prefixLength))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="address"/> and <paramref name="network"/> share their first <paramref name="prefixLength"/>
    /// bits, and are the same family. Only the address bytes count: an IPv6 scope id says which link, not which subnet.
    /// A prefix length outside the address's own bit count (a garbled adapter) matches nothing.
    /// </summary>
    internal static bool InSubnet(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }
        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        if (prefixLength < 0 || prefixLength > a.Length * 8)
        {
            return false;
        }

        var whole = prefixLength / 8;
        for (var i = 0; i < whole; i++)
        {
            if (a[i] != n[i])
            {
                return false;
            }
        }
        var rest = prefixLength % 8;
        if (rest == 0)
        {
            return true;
        }
        var mask = (byte)(0xFF << (8 - rest));
        return (a[whole] & mask) == (n[whole] & mask);
    }

    private static IReadOnlyList<(IPAddress Address, int PrefixLength)> ReadAdapters()
    {
        var subnets = new List<(IPAddress, int)>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    {
                        subnets.Add((unicast.Address, unicast.PrefixLength));
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Whatever was read before the failure stands; no adapters is "on no link".
        }
        return subnets;
    }
}
