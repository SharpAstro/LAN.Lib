using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

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
    Task BroadcastAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Raised for every discovery datagram received (on a background thread).</summary>
    event Action<DiscoveryDatagram>? DatagramReceived;

    /// <summary>
    /// Null when the transport is fully working; otherwise one sentence saying what it cannot do and why --
    /// for the UDP transport, that the discovery port could not be bound, so this node announces itself but
    /// will never see a peer. A default so that a bespoke or fake transport need not know the concept. A host
    /// should log it once at start-up; nothing else in the library acts on it, because there is nothing to
    /// act on -- discovery is best-effort by nature and the application must be usable without it.
    /// </summary>
    string? Degradation => null;
}
