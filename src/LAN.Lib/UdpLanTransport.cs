using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LAN.Lib;

/// <summary>
/// The real <see cref="ILanTransport"/>: one UDP socket bound to the discovery port for both send and
/// receive. Two socket-setup details matter: <c>ReuseAddress</c> so several apps on one host (and our
/// own send+receive) can share the discovery port, and <c>EnableBroadcast</c> so sending to
/// 255.255.255.255 is permitted.
/// </summary>
public sealed class UdpLanTransport : ILanTransport
{
    private readonly UdpClient _udp;
    private readonly int _discoveryPort;
    private readonly CancellationTokenSource _cts = new();

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

    public void Broadcast(string text)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, _discoveryPort));
        }
        catch
        {
            // No network / broadcast unavailable — discovery simply shows no peers, never crashes.
        }
    }

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
