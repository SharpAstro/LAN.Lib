using System;
using System.Collections.Generic;

namespace LAN.Lib;

/// <summary>
/// Configuration for <see cref="LanDiscovery"/> / <see cref="ServiceCollectionExtensions.AddLanDiscovery"/>.
/// </summary>
public sealed class LanDiscoveryOptions
{
    /// <summary>The service name we announce and that consumers filter on (e.g. "tianwen-server").
    /// Required when <see cref="Announce"/> is true.</summary>
    public string ServiceName { get; set; } = "";

    /// <summary>The port our service listens on, announced in the beacon so a peer knows where to open
    /// its session/control channel (discovery itself is UDP on <see cref="LanProtocol.DiscoveryPort"/>).</summary>
    public int ServicePort { get; set; }

    /// <summary>The UDP port discovery itself runs on; <see cref="LanProtocol.DiscoveryPort"/> unless every node
    /// on this LAN is told otherwise (it is a shared broadcast domain, so a lone deviation hears nobody).
    /// Exists for the box where even the well-known port is reserved, and for tests.</summary>
    public int DiscoveryPort { get; set; } = LanProtocol.DiscoveryPort;

    /// <summary>Human display name for this node, read fresh each beacon so a change propagates.
    /// Defaults to the machine name.</summary>
    public string NodeName { get; set; } = Environment.MachineName;

    /// <summary>Path to a file holding this node's <b>stable</b> id (minted once, then reused). Set on a
    /// node that consumers bind to across restarts; leave null for a pure consumer/monitor.</summary>
    public string? StableNodeIdPath { get; set; }

    /// <summary>When false, listen only — never broadcast an announce or a bye. A pure monitor client
    /// that wants to see peers without appearing as one itself.</summary>
    public bool Announce { get; set; } = true;

    /// <summary>When false, announce only — never subscribe to the transport's receive stream, so
    /// <see cref="IPeerTable.Peers"/> stays permanently empty. The mirror of <see cref="Announce"/>,
    /// for a node that publishes itself but has no business knowing who else is out there: it makes
    /// "who discovers whom" one-way by construction rather than by convention (a headless service that
    /// is only ever the <i>target</i> of a bind should not accumulate a table of its own clients).
    /// Setting both this and <see cref="Announce"/> to false leaves an inert instance.</summary>
    public bool Listen { get; set; } = true;

    /// <summary>Extra facts to advertise in every announce (e.g. a version or capability flag), on top
    /// of the well-known machine/pid/node properties. Keys must be simple identifiers.</summary>
    public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();
}
