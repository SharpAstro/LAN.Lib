using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace LAN.Lib;

/// <summary>
/// The symmetric heart of discovery: periodically broadcasts our own announce beacon <i>and</i> listens
/// for everyone else's, keeping a live, self-expiring table of peers. Time is driven by
/// <see cref="TimeProvider"/> so beacon cadence and staleness are deterministic in tests.
///
/// <para>Broadcast, receive, expiry and the well-known-property mapping (stable node id, machine, pid)
/// all live here; <see cref="ServiceCollectionExtensions.AddLanDiscovery"/> wraps it in a hosted service
/// for the generic host, but the class itself needs no host and is tested directly with a fake transport
/// and a <c>FakeTimeProvider</c>.</para>
/// </summary>
public sealed class LanDiscovery : IPeerTable, IDisposable
{
    /// <summary>How often we re-announce ourselves and prune stale peers.</summary>
    public static readonly TimeSpan BeaconInterval = TimeSpan.FromSeconds(1);

    /// <summary>A peer unheard-from for this long is dropped from the table.</summary>
    public static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(5);

    private readonly ILanTransport _transport;
    private readonly TimeProvider _time;
    private readonly LanDiscoveryOptions _options;
    private readonly LanIdentity _identity;
    // Process constants sent in every beacon purely so consumers can disambiguate look-alike names.
    private readonly string _machineName = Environment.MachineName;
    private readonly int _pid = Environment.ProcessId;
    private readonly ConcurrentDictionary<string, LanPeer> _peers = new();
    private ITimer? _beacon;

    public LanDiscovery(ILanTransport transport, TimeProvider time, LanDiscoveryOptions options, LanIdentity identity)
    {
        _transport = transport;
        _time = time;
        _options = options;
        _identity = identity;
        _transport.DatagramReceived += OnDatagram;
    }

    /// <summary>Raised whenever the set of peers changes (add / bye / expire).</summary>
    public event Action? Changed;

    /// <summary>Begin beaconing (immediately, then every <see cref="BeaconInterval"/>).</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await SendBeaconAsync(cancellationToken).ConfigureAwait(false);
        // The timer callback itself can't be async, so each tick fires-and-forgets a beacon; the
        // transport swallows send failures, so there's nothing here to observe or rethrow.
        _beacon = _time.CreateTimer(_ => _ = SendBeaconAsync(), null, BeaconInterval, BeaconInterval);
    }

    /// <inheritdoc />
    public string NodeId => _identity.NodeId;

    /// <inheritdoc />
    public IReadOnlyList<LanPeer> Peers => SortedSnapshot(_peers.Values);

    /// <inheritdoc />
    public IReadOnlyList<LanPeer> PeersOf(string service) =>
        SortedSnapshot(_peers.Values.Where(p => string.Equals(p.Service, service, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Announce that we're leaving so peers drop us promptly (expiry is the fallback). No-op
    /// when listen-only.</summary>
    public Task SendByeAsync(CancellationToken cancellationToken = default) =>
        _options.Announce
            ? _transport.BroadcastAsync(LanProtocol.EncodeBye(_identity.PeerId), cancellationToken)
            : Task.CompletedTask;

    private static LanPeer[] SortedSnapshot(IEnumerable<LanPeer> peers) =>
        // Stable order that matches ResolveLabels' numbering: name, then machine, then PID.
        peers
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.MachineName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Pid)
            .ToArray();

    private async Task SendBeaconAsync(CancellationToken cancellationToken = default)
    {
        if (_options.Announce)
        {
            await _transport.BroadcastAsync(
                LanProtocol.EncodeAnnounce(
                    _identity.PeerId, _options.ServiceName, _options.ServicePort, _options.NodeName, BuildProperties()),
                cancellationToken).ConfigureAwait(false);
        }

        Prune();
    }

    private IReadOnlyDictionary<string, string> BuildProperties()
    {
        var props = new Dictionary<string, string>(_options.Properties)
        {
            [LanProtocol.PropMachineName] = _machineName,
            [LanProtocol.PropPid] = _pid.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(_identity.NodeId))
            props[LanProtocol.PropNodeId] = _identity.NodeId;
        return props;
    }

    private void OnDatagram(DiscoveryDatagram dg)
    {
        var msg = LanProtocol.Parse(dg.Text);
        switch (msg.Kind)
        {
            case LanMessageKind.Announce:
                if (msg.PeerId == _identity.PeerId)
                    return; // our own beacon echoes back to us — ignore it
                AddOrRefresh(msg, dg.SenderAddress);
                break;

            case LanMessageKind.Bye:
                Remove(msg.PeerId);
                break;
        }
    }

    private void AddOrRefresh(LanMessage msg, IPAddress senderAddress)
    {
        var props = msg.Properties ?? new Dictionary<string, string>(0);
        var peer = new LanPeer(
            PeerId: msg.PeerId,
            Service: msg.Service,
            Name: msg.Name,
            EndPoint: new IPEndPoint(senderAddress, msg.Port),
            NodeId: props.TryGetValue(LanProtocol.PropNodeId, out var node) ? node : "",
            MachineName: props.TryGetValue(LanProtocol.PropMachineName, out var mach) ? mach : "",
            Pid: props.TryGetValue(LanProtocol.PropPid, out var pid) && int.TryParse(pid, out var p) ? p : 0,
            Properties: props,
            LastSeen: _time.GetUtcNow());

        // Not a single atomic op, but OnDatagram only ever runs on the transport's one receive thread,
        // so writes are already serialized; ConcurrentDictionary just makes them safe to read concurrently.
        var added = _peers.TryAdd(peer.PeerId, peer);
        if (!added)
            _peers[peer.PeerId] = peer;

        // Only a genuinely new peer changes the set; a periodic refresh of a known peer does not.
        if (added)
            Changed?.Invoke();
    }

    private void Remove(string peerId)
    {
        if (_peers.TryRemove(peerId, out _))
            Changed?.Invoke();
    }

    private void Prune()
    {
        var cutoff = _time.GetUtcNow() - PeerTimeout;
        var stale = _peers.Where(kv => kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToArray();
        foreach (var id in stale)
            _peers.TryRemove(id, out _);

        if (stale.Length > 0)
            Changed?.Invoke();
    }

    public void Dispose()
    {
        _beacon?.Dispose();
        _transport.DatagramReceived -= OnDatagram;
    }
}
