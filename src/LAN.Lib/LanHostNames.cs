using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LAN.Lib;

/// <summary>The two lookups <see cref="LanHostNames"/> is made of, so a test can answer them.</summary>
public interface IHostNameResolver
{
    /// <summary>The name <paramref name="address"/> reverse-resolves to, or null when it has none.</summary>
    Task<string?> ReverseAsync(IPAddress address, CancellationToken cancellationToken);

    /// <summary>The addresses <paramref name="hostName"/> resolves to, or none.</summary>
    Task<IReadOnlyList<IPAddress>> ForwardAsync(string hostName, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IHostNameResolver"/> through the machine's own resolver (<see cref="Dns"/>), which on a home LAN answers
/// from the router's DHCP registrations, and on Windows from NetBIOS and LLMNR as well, and on a Mac or a Linux box with
/// mDNS from <c>.local</c> names.
/// </summary>
public sealed class DnsHostNameResolver : IHostNameResolver
{
    /// <inheritdoc/>
    public async Task<string?> ReverseAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            // An address passed as the string is a reverse lookup.
            var entry = await Dns.GetHostEntryAsync(address.ToString(), cancellationToken).ConfigureAwait(false);
            // With no name, some resolvers answer the address itself as the name, which names nothing.
            return string.IsNullOrWhiteSpace(entry.HostName) || IPAddress.TryParse(entry.HostName, out _) ? null : entry.HostName;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IPAddress>> ForwardAsync(string hostName, CancellationToken cancellationToken)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(hostName, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }
    }
}

/// <summary>
/// Who an address is on the LAN, by name: the name it reverse-resolves to, counted ONLY when that name resolves back to
/// the same address (forward-confirmed). A peer's address changes with every DHCP renewal and its confirmed name does
/// not, so a name is what an application may remember a peer by (TianWen's "always allow this host"); a PTR record alone
/// is not, since whoever runs the zone writes it.
/// <para><b>Cached per address, a miss included</b>, for <see cref="CacheLifetime"/>: an application polling many times a
/// second costs one lookup, and an address with no name is not asked again every request. <b>Bounded</b> by
/// <see cref="LookupBudget"/>: a resolver that never answers gives no name rather than holding the request that asked.
/// The lookup runs on its own budget, never the caller's token, so a caller that gives up does not leave a cancelled
/// answer in the cache for the next one.</para>
/// <para>A name is still a claim made on the LAN: whoever controls a DHCP registration or the resolver can make one. It
/// is a stable handle, not a proof, which is as far as plain HTTP goes either way.</para>
/// </summary>
public sealed class LanHostNames(IHostNameResolver resolver, TimeProvider timeProvider)
{
    /// <summary>How long an address's answer, a name or none, is reused.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long a lookup, both halves together, may take before the address counts as having no name.</summary>
    public static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<IPAddress, Cached> _cache = new ConcurrentDictionary<IPAddress, Cached>();

    /// <summary>The forward-confirmed name of <paramref name="address"/>, lower case and without a trailing dot, or null.</summary>
    public Task<string?> ConfirmedNameOfAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var key = Normalise(address);
        while (true)
        {
            var now = timeProvider.GetUtcNow().UtcTicks;
            var found = _cache.TryGetValue(key, out var cached);
            if (found && cached is { } fresh && now < fresh.ExpiresTicks)
            {
                return fresh.Name.WaitAsync(cancellationToken);
            }

            // Published before the lookup starts, so callers racing for the same address wait on one lookup rather
            // than each starting its own.
            var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entry = new Cached(answer.Task, now + CacheLifetime.Ticks);
            var published = found && cached is { } stale ? _cache.TryUpdate(key, entry, stale) : _cache.TryAdd(key, entry);
            if (!published)
            {
                continue;
            }
            _ = LookUpAsync(key, answer);
            return answer.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task LookUpAsync(IPAddress address, TaskCompletionSource<string?> answer)
    {
        using var budget = new CancellationTokenSource(LookupBudget, timeProvider);
        try
        {
            answer.TrySetResult(await ConfirmAsync(address, budget.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            answer.TrySetResult(null);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            answer.TrySetResult(null);
        }
        finally
        {
            // Whatever else went wrong, the answer completes: everyone asking about this address waits on it.
            answer.TrySetResult(null);
        }
    }

    private async Task<string?> ConfirmAsync(IPAddress address, CancellationToken cancellationToken)
    {
        // WaitAsync, so the budget holds even for a resolver that ignores its token.
        if (await resolver.ReverseAsync(address, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false) is not { } reversed
            || NormaliseName(reversed) is not { } name)
        {
            return null;
        }
        foreach (var candidate in await resolver.ForwardAsync(name, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Normalise(candidate).Equals(address))
            {
                return name;
            }
        }
        return null;
    }

    /// <summary>An IPv4 address seen through an IPv6 socket (<c>::ffff:192.168.1.23</c>) is the IPv4 address.</summary>
    private static IPAddress Normalise(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string? NormaliseName(string name)
    {
        var trimmed = name.Trim().TrimEnd('.');
        return trimmed.Length == 0 ? null : trimmed.ToLowerInvariant();
    }

    private sealed record Cached(Task<string?> Name, long ExpiresTicks);
}
