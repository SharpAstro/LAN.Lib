using System;
using System.Net;
using System.Net.Sockets;

namespace LAN.Lib;

/// <summary>Which address a node is reachable at on its LAN, as against the one a datagram happened to leave from.</summary>
public static class LanAddress
{
    /// <summary>
    /// The IPv4 address this machine's default route leaves by, or null when it has none (no network). Found by
    /// connecting a UDP socket, which sends nothing and only asks the routing table, so a WSL/Hyper-V/Docker
    /// virtual adapter (a 172.x address with no default route) is never the answer.
    /// </summary>
    public static IPAddress? DefaultRoute()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));
            return (socket.LocalEndPoint as IPEndPoint)?.Address is { } address && IsReachable(address) ? address : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>An IPv4 address a peer could be told to connect to: not unspecified, loopback, link-local or multicast.</summary>
    public static bool IsReachable(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.Any.Equals(address) || IPAddress.IsLoopback(address))
        {
            return false;
        }
        var b = address.GetAddressBytes();
        return !(b[0] == 169 && b[1] == 254) && b[0] < 224;
    }

    /// <summary>A private (RFC 1918) IPv4 address: the kind a virtual adapter or a LAN gives.</summary>
    internal static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
