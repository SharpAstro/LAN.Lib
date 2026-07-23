using System;
using System.Net;

namespace LAN.Lib;

/// <summary>A raw discovery datagram plus the address it came from. The sender's IP becomes the host
/// half of the peer's service endpoint (the announce only carries the port), so a peer is always
/// reachable at the address we actually heard it from.</summary>
public readonly record struct DiscoveryDatagram(string Text, IPAddress SenderAddress);

/// <summary>
/// The one socket peer-discovery needs: UDP broadcast to announce ourselves and to hear everyone
/// else, behind an interface so <see cref="LanDiscovery"/> is unit-testable against an in-memory fake
/// with no real network. <see cref="UdpLanTransport"/> is the real backend.
///
/// <para>Discovery is UDP-only by design. A session/control channel (TCP, WebSocket, HTTP, …) is the
/// consuming application's concern and lives outside this library — discovery's whole job is to tell an
/// app <i>which address:port</i> to open that channel to.</para>
/// </summary>
public interface ILanTransport : IAsyncDisposable
{
    /// <summary>Broadcast a discovery datagram to the whole subnet.</summary>
    void Broadcast(string text);

    /// <summary>Raised for every discovery datagram received (on a background thread).</summary>
    event Action<DiscoveryDatagram>? DatagramReceived;
}
