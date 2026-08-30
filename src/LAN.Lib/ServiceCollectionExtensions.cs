using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LAN.Lib;

/// <summary>DI wiring for LAN peer discovery.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register LAN peer discovery: a UDP transport, the <see cref="LanDiscovery"/> beacon + peer table
    /// (as <see cref="IPeerTable"/>), and a hosted service that runs them for the host's lifetime.
    ///
    /// <para>The <see cref="ILanTransport"/> is registered with <c>TryAdd</c>, so a test (or an app with
    /// a bespoke transport) can register its own <see cref="ILanTransport"/> beforehand and this leaves
    /// it in place. The <see cref="TimeProvider"/> is resolved from DI when present, else
    /// <see cref="TimeProvider.System"/>.</para>
    /// </summary>
    public static IServiceCollection AddLanDiscovery(this IServiceCollection services, Action<LanDiscoveryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new LanDiscoveryOptions();
        configure(options);
        services.AddSingleton(options);

        services.TryAddSingleton<ILanTransport>(sp => new UdpLanTransport(sp.GetRequiredService<LanDiscoveryOptions>().DiscoveryPort));

        services.AddSingleton(sp =>
        {
            var transport = sp.GetRequiredService<ILanTransport>();
            var time = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var opts = sp.GetRequiredService<LanDiscoveryOptions>();
            var identity = LanIdentity.Create(opts.StableNodeIdPath);
            return new LanDiscovery(transport, time, opts, identity);
        });
        services.AddSingleton<IPeerTable>(sp => sp.GetRequiredService<LanDiscovery>());

        services.AddHostedService<LanDiscoveryHostedService>();

        return services;
    }
}
