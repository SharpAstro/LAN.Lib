using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LAN.Lib;

/// <summary>
/// The real <see cref="ILanTransport"/>: one UDP socket bound to the discovery port for both send and
/// receive. Two socket-setup details matter: <c>ReuseAddress</c> so several apps on one host (and our
/// own send+receive) can share the discovery port, and <c>EnableBroadcast</c> so broadcasting is
/// permitted.
///
/// <para>Announcing sends one datagram PER INTERFACE, to that interface's directed broadcast address,
/// not a single one to 255.255.255.255. Receiving is bound to <see cref="IPAddress.Any"/> and so hears
/// every interface, but a limited broadcast is sent out exactly ONE of them — whichever wins the route
/// — and on a multi-homed host that is rarely the LAN. Measured on a developer box with nine IPv4
/// addresses: <c>Find-NetRoute -RemoteIPAddress 255.255.255.255</c> resolved to the Hyper-V
/// "Default Switch" (192.168.240.1), so every announce went to a virtual network with no other players
/// while the real Wi-Fi LAN never heard one. The asymmetry is the tell — that host still SAW its peers
/// (receive is on all interfaces) but was invisible to them, a one-way lobby. Hyper-V, WSL, Docker or
/// a VPN is enough to trigger it, so it is the common case, not an exotic one.</para>
/// </summary>
public sealed class UdpLanTransport : ILanTransport
{
    /// <summary>How long a resolved target list is reused before being rebuilt. Interfaces come and go
    /// (Wi-Fi associates, a VPN connects), so the list cannot be built once at construction; but
    /// enumerating them on every beacon is needless work, and the beacon repeats far faster than a
    /// network changes.</summary>
    private static readonly TimeSpan TargetRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly UdpClient _udp;
    private readonly int _discoveryPort;
    private readonly CancellationTokenSource _cts = new();
    private IPEndPoint[] _targets = [];
    private long _targetsExpireAtTicks;

    public event Action<DiscoveryDatagram>? DatagramReceived;

    public UdpLanTransport(int discoveryPort = LanProtocol.DiscoveryPort)
    {
        _discoveryPort = discoveryPort;
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
        _udp.EnableBroadcast = true;

        _ = ReceiveLoopAsync(_cts.Token);
    }

    public async Task BroadcastAsync(string text, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        foreach (var target in ResolveTargets())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _udp.SendAsync(bytes, target, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Per TARGET, so one unroutable interface cannot stop the others being announced to.
                // A down-but-still-enumerated adapter is the ordinary case here, not an error worth
                // surfacing: discovery just shows no peers on that network.
            }
        }
    }

    /// <summary>
    /// One endpoint per network we can announce on: each up, non-loopback interface's IPv4 directed
    /// broadcast address, plus 255.255.255.255 as a floor.
    ///
    /// <para>The limited broadcast is kept because the per-interface list can legitimately come back
    /// empty — a platform that refuses <see cref="UnicastIPAddressInformation.IPv4Mask"/>, a container
    /// with an unusual stack — and on a single-homed host it is exactly equivalent, so keeping it costs
    /// one duplicate datagram and removes the chance of this change making a working setup silent.</para>
    /// </summary>
    private IPEndPoint[] ResolveTargets()
    {
        var now = Environment.TickCount64;
        var cached = _targets;
        if (cached.Length > 0 && now < Interlocked.Read(ref _targetsExpireAtTicks))
        {
            return cached;
        }

        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, _discoveryPort) };
        var seen = new HashSet<uint>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    IPAddress? mask;
                    try
                    {
                        mask = unicast.IPv4Mask;
                    }
                    catch
                    {
                        continue; // not every platform exposes a mask; that interface just misses out
                    }
                    if (mask is null || mask.Equals(IPAddress.Any)) continue;

                    // A /32 (point-to-point, some VPN adapters) has no meaningful broadcast address:
                    // the "directed broadcast" degenerates to the interface's own address.
                    if (DirectedBroadcast(unicast.Address, mask) is not { } directed) continue;
                    if (!seen.Add(ToUInt32(directed))) continue;

                    targets.Add(new IPEndPoint(directed, _discoveryPort));
                }
            }
        }
        catch
        {
            // Interface enumeration is best-effort: fall back to the limited broadcast alone.
        }

        var resolved = targets.ToArray();
        _targets = resolved;
        Interlocked.Exchange(ref _targetsExpireAtTicks, now + (long)TargetRefreshInterval.TotalMilliseconds);
        return resolved;
    }

    /// <summary>
    /// The directed broadcast address for an interface: host bits all set (<c>address | ~mask</c>).
    /// <see langword="null"/> when there is no meaningful one — a /32, where it would just be the
    /// interface's own address and sending to it would announce only to ourselves.
    /// </summary>
    internal static IPAddress? DirectedBroadcast(IPAddress address, IPAddress mask)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return null;
        if (mask.AddressFamily != AddressFamily.InterNetwork) return null;

        var host = ToUInt32(address);
        var directed = host | ~ToUInt32(mask);
        return directed == host ? null : new IPAddress(ToOctets(directed));
    }

    /// <summary>Host-order value of a dotted-quad, so masking is plain arithmetic.</summary>
    private static uint ToUInt32(IPAddress address)
    {
        Span<byte> octets = stackalloc byte[4];
        address.TryWriteBytes(octets, out _);
        return ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];
    }

    /// <summary>Back to dotted-quad order. Deliberately not <c>new IPAddress(long)</c>, which takes a
    /// NETWORK-order value and would silently reverse the octets.</summary>
    private static byte[] ToOctets(uint value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(ct);
                var text = Encoding.UTF8.GetString(result.Buffer);
                DatagramReceived?.Invoke(new DiscoveryDatagram(text, result.RemoteEndPoint.Address));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { /* transient — keep listening */ }
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _udp.Dispose(); } catch { /* best-effort */ }
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
