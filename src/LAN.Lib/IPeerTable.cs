using System;
using System.Collections.Generic;

namespace LAN.Lib;

/// <summary>
/// The read surface over the live set of discovered peers — what a UI polls (or subscribes to) to show
/// "who's on the LAN". Kept separate from <see cref="LanDiscovery"/> (which implements it) so a consumer
/// takes a dependency on the read-only view, not the beacon machinery.
/// </summary>
public interface IPeerTable
{
    /// <summary>All currently-visible peers, in a stable order (name, then machine, then PID). A fresh
    /// snapshot each call — safe to iterate without locking.</summary>
    IReadOnlyList<LanPeer> Peers { get; }

    /// <summary>Just the peers advertising <paramref name="service"/> (case-insensitive), in the same
    /// stable order. The shared broadcast domain means several services can appear at once; this is how
    /// a consumer narrows to the one it cares about (e.g. "tianwen-server").</summary>
    IReadOnlyList<LanPeer> PeersOf(string service);

    /// <summary>Raised (on a background thread) whenever the set of peers changes — a peer appears, says
    /// bye, or expires. A hint to re-read <see cref="Peers"/>; a periodic refresh of an existing peer
    /// does not raise it.</summary>
    event Action? Changed;
}
