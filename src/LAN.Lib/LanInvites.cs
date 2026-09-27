using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace LAN.Lib;

/// <summary>An invite waiting for its answer: who asked (<see cref="Label"/>, for a person to read), and whatever the
/// application needs to act on the answer (<see cref="Payload"/>: a pending connection, a requester's details).</summary>
public sealed record LanInvite<T>(string Id, string Label, T Payload, DateTimeOffset OfferedAt);

/// <summary>Where an invite stands, as its asker reads it back by id.</summary>
public enum LanInviteOutcome
{
    /// <summary>Nothing is known under that id: never offered, or answered long enough ago to be forgotten.</summary>
    Unknown,

    /// <summary>Waiting for an answer.</summary>
    Pending,

    /// <summary>Accepted.</summary>
    Accepted,

    /// <summary>Declined.</summary>
    Declined,

    /// <summary>It ended with no answer: the asker withdrew it, or stopped being seen.</summary>
    Withdrawn,
}

/// <summary>
/// The answering half of an invite handshake, and no transport: one invite waits at a time, and a person answers it.
/// Lifted out of the chess lobby's LAN play, where an invite arrives on a TCP connection and the connection becomes the
/// game, so that an application whose request arrives another way (an HTTP route, answered by a different client of the
/// same machine) keeps the same rules rather than a second copy of them.
/// <para><b>One at a time.</b> A second offer while one waits is refused (<see cref="TryOffer"/> answers false), since a
/// person answers one question at a time; the asker is told the other side is busy.</para>
/// <para><b>An invite lives only while its asker is there.</b> Its application says so either way it can: by
/// <see cref="Withdraw"/> when it learns the asker left (a socket closing), or by <see cref="Seen"/> each time the asker
/// shows it is still there (a poll), with a presence lapse after which an invite not seen is withdrawn. An invite whose
/// asker walked away must not be accepted later: whatever the acceptance grants would go to nobody.</para>
/// <para><b>An answer names the invite it answers.</b> <see cref="Answer"/> takes the id, so an answer given to an invite
/// that has meanwhile been withdrawn, and replaced by another, answers nothing rather than the newer one.</para>
/// <para><b>The asker reads the outcome back by id</b> (<see cref="OutcomeOf"/>) for <see cref="OutcomeRetention"/>
/// after the answer, so an asker that polls learns it without a connection held open.</para>
/// <para>Threading: an offer, an answer and a withdrawal arrive on different threads, and a user interface reads
/// <see cref="Pending"/> every frame, so the whole state is one immutable value replaced by compare-and-swap: every read
/// is lock-free, and none blocks a render thread.</para>
/// </summary>
public sealed class LanInvites<T>
{
    /// <summary>How long an answered invite's outcome stays readable by its asker.</summary>
    public static readonly TimeSpan OutcomeRetention = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _timeProvider;
    private readonly long _presenceLapseTicks;
    private State _state = new State(null, 0, ImmutableDictionary<string, Answered>.Empty);

    /// <param name="presenceLapse">How long an invite waits without its asker being <see cref="Seen"/> before it is
    /// withdrawn; <see cref="Timeout.InfiniteTimeSpan"/> for an application that withdraws it itself.</param>
    public LanInvites(TimeProvider timeProvider, TimeSpan presenceLapse)
    {
        if (presenceLapse <= TimeSpan.Zero && presenceLapse != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(presenceLapse), presenceLapse, "A presence lapse must be positive, or infinite");
        }
        _timeProvider = timeProvider;
        _presenceLapseTicks = presenceLapse == Timeout.InfiniteTimeSpan ? long.MaxValue : presenceLapse.Ticks;
    }

    /// <summary>
    /// Raised when the invite waiting changes: one offered, answered or withdrawn. A lapse is found when the state is
    /// next touched, or by <see cref="Sweep"/>, and is raised then. Raised outside any state change, on the thread that
    /// made it.
    /// </summary>
    public event Action? Changed;

    /// <summary>The invite waiting for an answer, if one is.</summary>
    public LanInvite<T>? Pending => Tidy(Volatile.Read(ref _state), Now).Pending;

    /// <summary>
    /// Offers an invite from <paramref name="label"/>: true with the <paramref name="invite"/> now waiting, false when
    /// another one is waiting already.
    /// </summary>
    public bool TryOffer(string label, T payload, [NotNullWhen(true)] out LanInvite<T>? invite)
    {
        LanInvite<T>? offered = null;
        Update((state, now) =>
        {
            if (state.Pending is not null)
            {
                offered = null;
                return state;
            }
            offered = new LanInvite<T>(Guid.CreateVersion7().ToString("N"), label, payload, new DateTimeOffset(now, TimeSpan.Zero));
            return state with { Pending = offered, PendingSeenTicks = now };
        }, out var lapsed);
        invite = offered;
        if (invite is not null || lapsed)
        {
            Changed?.Invoke();
        }
        return invite is not null;
    }

    /// <summary>The asker of <paramref name="id"/> is still there: true while that invite waits, false once it does not.</summary>
    public bool Seen(string id)
    {
        var waiting = false;
        Update((state, now) =>
        {
            waiting = state.Pending?.Id == id;
            return waiting ? state with { PendingSeenTicks = now } : state;
        }, out var lapsed);
        if (lapsed)
        {
            Changed?.Invoke();
        }
        return waiting;
    }

    /// <summary>
    /// Answers the invite <paramref name="id"/>: the invite answered, or null when it is not the one waiting any more
    /// (withdrawn, lapsed, answered already, or never offered), in which case nothing was answered.
    /// </summary>
    public LanInvite<T>? Answer(string id, bool accept)
    {
        LanInvite<T>? answered = null;
        Update((state, now) =>
        {
            if (state.Pending is not { } pending || pending.Id != id)
            {
                answered = null;
                return state;
            }
            answered = pending;
            return Settle(state, now, accept ? LanInviteOutcome.Accepted : LanInviteOutcome.Declined);
        }, out var lapsed);
        if (answered is not null || lapsed)
        {
            Changed?.Invoke();
        }
        return answered;
    }

    /// <summary>Withdraws the invite <paramref name="id"/>, its asker gone or changed its mind: true when it was the one waiting.</summary>
    public bool Withdraw(string id)
    {
        var withdrawn = false;
        Update((state, now) =>
        {
            withdrawn = state.Pending?.Id == id;
            return withdrawn ? Settle(state, now, LanInviteOutcome.Withdrawn) : state;
        }, out var lapsed);
        if (withdrawn || lapsed)
        {
            Changed?.Invoke();
        }
        return withdrawn;
    }

    /// <summary>Where the invite <paramref name="id"/> stands, as its asker reads it back.</summary>
    public LanInviteOutcome OutcomeOf(string id)
    {
        var state = Tidy(Volatile.Read(ref _state), Now);
        if (state.Pending?.Id == id)
        {
            return LanInviteOutcome.Pending;
        }
        return state.Answered.TryGetValue(id, out var answered) ? answered.Outcome : LanInviteOutcome.Unknown;
    }

    /// <summary>Withdraws a waiting invite whose asker has not been seen within the presence lapse, raising
    /// <see cref="Changed"/> when it does. For an application that must say so promptly rather than at the next touch.</summary>
    public void Sweep()
    {
        Update((state, _) => state, out var lapsed);
        if (lapsed)
        {
            Changed?.Invoke();
        }
    }

    private long Now => _timeProvider.GetUtcNow().UtcTicks;

    /// <summary>
    /// Applies <paramref name="change"/> to the state as it is now, tidied, and swaps the result in.
    /// <paramref name="lapsed"/> says whether tidying withdrew a waiting invite, which is a change of its own. The change
    /// may run more than once when writers race, so it only computes: a caller reads what it decided only once this returns.
    /// </summary>
    private void Update(Func<State, long, State> change, out bool lapsed)
    {
        while (true)
        {
            var before = Volatile.Read(ref _state);
            var now = Now;
            var tidied = Tidy(before, now);
            var after = change(tidied, now);
            if (ReferenceEquals(after, before))
            {
                lapsed = false;
                return;
            }
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, after, before), before))
            {
                lapsed = before.Pending is not null && tidied.Pending is null;
                return;
            }
        }
    }

    /// <summary>The state with a lapsed invite withdrawn and long-answered outcomes forgotten; the same instance when neither applies.</summary>
    private State Tidy(State state, long now)
    {
        var tidied = state;
        if (tidied.Pending is not null && now - tidied.PendingSeenTicks > _presenceLapseTicks)
        {
            tidied = Settle(tidied, now, LanInviteOutcome.Withdrawn);
        }
        foreach (var (id, answered) in tidied.Answered)
        {
            if (now - answered.AtTicks > OutcomeRetention.Ticks)
            {
                tidied = tidied with { Answered = tidied.Answered.Remove(id) };
            }
        }
        return tidied;
    }

    private static State Settle(State state, long now, LanInviteOutcome outcome) =>
        state.Pending is { } pending
            ? new State(null, 0, state.Answered.SetItem(pending.Id, new Answered(outcome, now)))
            : state;

    private sealed record State(LanInvite<T>? Pending, long PendingSeenTicks, ImmutableDictionary<string, Answered> Answered);

    private readonly record struct Answered(LanInviteOutcome Outcome, long AtTicks);
}
