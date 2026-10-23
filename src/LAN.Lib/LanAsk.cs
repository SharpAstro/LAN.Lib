using System;
using System.Threading;
using System.Threading.Tasks;

namespace LAN.Lib;

/// <summary>What the offer of an ask came back as: the ticket's <see cref="Id"/> when the node took it, else why not.</summary>
public readonly record struct LanAskOffer(string? Id, string? Error = null)
{
    /// <summary>The node took the ask and the ticket is <see cref="Id"/>.</summary>
    public bool Accepted => Id is { Length: > 0 };
}

/// <summary>One read of a ticket: where it stands, and the token once it has been accepted. A read that could not be made
/// at all (a dropped connection) is <see cref="LanInviteOutcome.Pending"/>: the loop reads again.</summary>
public readonly record struct LanAskReading(LanInviteOutcome Outcome, string? Token = null);

/// <summary>How an ask ended.</summary>
public enum LanAskOutcome
{
    /// <summary>The person allowed it, and the token is kept in the store.</summary>
    Accepted,

    /// <summary>The person denied it.</summary>
    Declined,

    /// <summary>It lapsed on the node (withdrawn, or no longer known there) before anyone answered.</summary>
    Lapsed,

    /// <summary>Nobody answered within the time allowed.</summary>
    TimedOut,

    /// <summary>The node would not take the ask (it is busy, or it refused); see <see cref="LanAskResult.Error"/>.</summary>
    Refused,
}

/// <summary>How an ask ended, and why a refusal was one.</summary>
public readonly record struct LanAskResult(LanAskOutcome Outcome, string? Error = null);

/// <summary>
/// The asking half of an invite, and no transport: offer, then read the ticket while it is pending, and keep the token the
/// first time it reads accepted. The wire (an HTTP route, a socket) is the caller's, handed in as two delegates, so the
/// loop is the same for every application and testable on a <see cref="TimeProvider"/>.
/// </summary>
public static class LanAsk
{
    /// <summary>How often a pending ticket is read.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    /// <summary>How long an ask waits for its person before giving up.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Asks <paramref name="endpoint"/> and waits for the answer. An accepted read with no token yet is read again, as a
    /// pending one is. The token is saved to <paramref name="store"/> before this returns <see cref="LanAskOutcome.Accepted"/>.
    /// </summary>
    /// <param name="endpoint">The node, <c>host:port</c>: the key the token is kept under.</param>
    /// <param name="offer">Makes the ask on the node.</param>
    /// <param name="read">Reads the ticket named by the id <paramref name="offer"/> gave.</param>
    /// <param name="store">Where the token goes.</param>
    /// <param name="time">The clock the interval and the deadline run on.</param>
    /// <param name="asked">Called once the node has taken the ask, before the first wait (to tell the person to look there).</param>
    /// <param name="interval">Between reads; <see cref="DefaultInterval"/> when null.</param>
    /// <param name="timeout">In all; <see cref="DefaultTimeout"/> when null.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    public static async Task<LanAskResult> RunAsync(
        string endpoint,
        Func<CancellationToken, Task<LanAskOffer>> offer,
        Func<string, CancellationToken, Task<LanAskReading>> read,
        ILanHeldTokens store,
        TimeProvider time,
        Action? asked = null,
        TimeSpan? interval = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var made = await offer(cancellationToken).ConfigureAwait(false);
        if (!made.Accepted)
        {
            return new LanAskResult(LanAskOutcome.Refused, made.Error);
        }
        asked?.Invoke();

        var wait = interval ?? DefaultInterval;
        var until = time.GetUtcNow() + (timeout ?? DefaultTimeout);
        while (time.GetUtcNow() < until)
        {
            await Task.Delay(wait, time, cancellationToken).ConfigureAwait(false);
            var reading = await read(made.Id!, cancellationToken).ConfigureAwait(false);
            switch (reading.Outcome)
            {
                case LanInviteOutcome.Accepted when reading.Token is { Length: > 0 } token:
                    store.Save(endpoint, token);
                    return new LanAskResult(LanAskOutcome.Accepted);
                case LanInviteOutcome.Declined:
                    return new LanAskResult(LanAskOutcome.Declined);
                case LanInviteOutcome.Withdrawn or LanInviteOutcome.Unknown:
                    return new LanAskResult(LanAskOutcome.Lapsed);
            }
        }
        return new LanAskResult(LanAskOutcome.TimedOut);
    }
}
