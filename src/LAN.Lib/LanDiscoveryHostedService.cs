using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace LAN.Lib;

/// <summary>
/// Runs <see cref="LanDiscovery"/> for the lifetime of the host: starts beaconing on startup and sends a
/// polite bye on shutdown (expiry is the fallback for an unclean exit). The beacon cadence itself is the
/// discovery timer's job — this service only bookends its lifetime.
/// </summary>
public sealed class LanDiscoveryHostedService(LanDiscovery discovery, ILogger<LanDiscoveryHostedService>? logger = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A transport that could not bind its port is running announce-only (see UdpLanTransport). That
        // is a fact about this box worth exactly one warning, not a reason to stop the host.
        if (discovery.Degradation is { } degradation)
        {
            logger?.LogWarning("{Degradation}", degradation);
        }
        await discovery.StartAsync(stoppingToken);
        try
        {
            // Nothing to loop — the discovery timer drives beaconing. Park until shutdown.
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await discovery.SendByeAsync(cancellationToken); } catch { /* best-effort courtesy */ }
        await base.StopAsync(cancellationToken);
    }
}
