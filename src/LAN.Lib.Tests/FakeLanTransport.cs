using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LAN.Lib;

namespace LAN.Lib.Tests;

/// <summary>
/// An in-memory stand-in for the LAN so <see cref="LanDiscovery"/> can be tested with no real sockets
/// (CI-safe). Transports created from one bus share it: a broadcast reaches every node's discovery
/// listener — including the sender, exactly as real UDP echoes a broadcast back (which is what exercises
/// the self-echo filter).
/// </summary>
internal sealed class FakeLanBus
{
    private readonly List<FakeLanTransport> _nodes = new();

    public FakeLanTransport CreateNode(string address)
    {
        var node = new FakeLanTransport(this, IPAddress.Parse(address));
        _nodes.Add(node);
        return node;
    }

    public void Broadcast(FakeLanTransport from, string text)
    {
        // Real UDP echoes a broadcast back to the sender too; LanDiscovery ignores its own peerId.
        foreach (var node in _nodes.ToArray())
            node.DeliverDatagram(new DiscoveryDatagram(text, from.Address));
    }
}

internal sealed class FakeLanTransport(FakeLanBus bus, IPAddress address) : ILanTransport
{
    public IPAddress Address { get; } = address;

    /// <summary>Every datagram this node has broadcast (for asserting beacon content / that a
    /// listen-only node stays silent).</summary>
    public List<string> Broadcasts { get; } = new();

    public event Action<DiscoveryDatagram>? DatagramReceived;

    public Task BroadcastAsync(string text, CancellationToken cancellationToken = default)
    {
        Broadcasts.Add(text);
        bus.Broadcast(this, text);
        return Task.CompletedTask;
    }

    /// <summary>Deliver a datagram straight to this node's listener (a remote peer's announce/bye).</summary>
    public void DeliverDatagram(DiscoveryDatagram dg) => DatagramReceived?.Invoke(dg);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
