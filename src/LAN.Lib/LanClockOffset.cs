using System;
using System.Threading;

namespace LAN.Lib;

/// <summary>
/// How far a peer's clock is ahead of this one, estimated from the answers it gives, and the two conversions that follow.
/// Transport-free like <see cref="LanInvites{T}"/>: the caller stamps its own clock before asking and after the answer
/// arrives, the peer stamps its own clock into the answer, and <see cref="Observe"/> takes the three.
/// <para>
/// <b>The estimate is NTP's</b>: the peer read its clock somewhere between the request leaving and the answer arriving, so
/// the best single guess is the midpoint, and the sample whose round trip was SHORTEST has the least room to be wrong by
/// (a slow answer says as much about the network as about the clock). Of the last <see cref="Window"/> samples the one
/// with the shortest round trip is kept, the newest on a tie, so the estimate follows a stepped clock within a window.
/// </para>
/// <para>
/// <b>A time the peer has no value for is not a time.</b> A peer reports <see cref="DateTimeOffset.MinValue"/> for
/// "never" (an exposure that has not started) and <see cref="DateTimeOffset.MaxValue"/> for "no end", and the
/// conversions leave both alone: shifting <c>MinValue</c> by a negative span throws, on whichever thread draws it. Any
/// other time is shifted and clamped to the representable range, never thrown from.
/// </para>
/// <para>
/// Lock-free: the state is one immutable record replaced by compare-and-swap, because a UI reads it every frame while a
/// poll writes it.
/// </para>
/// </summary>
public sealed class LanClockOffset
{
    /// <summary>How many samples <see cref="LanClockOffset(int)"/> keeps when it is not told.</summary>
    public const int DefaultWindow = 8;

    private sealed record State(Sample[] Samples, Sample Best);

    private readonly record struct Sample(long OffsetTicks, long RoundTripTicks);

    private static readonly State Empty = new([], default);

    private State _state = Empty;

    /// <param name="window">How many of the latest samples the estimate is chosen from; at least 1.</param>
    public LanClockOffset(int window = DefaultWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        Window = window;
    }

    /// <summary>How many of the latest samples the estimate is chosen from.</summary>
    public int Window { get; }

    /// <summary>True once one usable sample has been observed.</summary>
    public bool HasSample => Volatile.Read(ref _state).Samples.Length > 0;

    /// <summary>How far the peer's clock is ahead of this one (negative when it is behind); zero before the first sample.</summary>
    public TimeSpan Offset => TimeSpan.FromTicks(Volatile.Read(ref _state).Best.OffsetTicks);

    /// <summary>The round trip of the sample <see cref="Offset"/> comes from, which bounds its error; zero before the first sample.</summary>
    public TimeSpan RoundTrip => TimeSpan.FromTicks(Volatile.Read(ref _state).Best.RoundTripTicks);

    /// <summary>
    /// Takes one answer: <paramref name="sentAt"/> on this clock as the request left, <paramref name="peerNow"/> as the peer
    /// stamped it, <paramref name="receivedAt"/> on this clock as the answer arrived. An answer that arrived BEFORE it was
    /// asked for (this clock stepped back in between) says nothing and is dropped.
    /// </summary>
    public void Observe(DateTimeOffset sentAt, DateTimeOffset peerNow, DateTimeOffset receivedAt)
    {
        var roundTrip = receivedAt.UtcTicks - sentAt.UtcTicks;
        if (roundTrip < 0)
        {
            return;
        }

        var sample = new Sample(peerNow.UtcTicks - (sentAt.UtcTicks + roundTrip / 2), roundTrip);
        while (true)
        {
            var before = Volatile.Read(ref _state);
            var keep = Math.Min(before.Samples.Length, Window - 1);
            var samples = new Sample[keep + 1];
            Array.Copy(before.Samples, before.Samples.Length - keep, samples, 0, keep);
            samples[keep] = sample;

            // The shortest round trip wins; on a tie the newer sample does, so a clock that stepped is followed.
            var best = samples[0];
            foreach (var s in samples)
            {
                if (s.RoundTripTicks <= best.RoundTripTicks)
                {
                    best = s;
                }
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, new State(samples, best), before), before))
            {
                return;
            }
        }
    }

    /// <summary>The peer's <paramref name="peerTime"/> on this computer's clock (UTC); a <c>MinValue</c> or <c>MaxValue</c> is returned as it is.</summary>
    public DateTimeOffset ToLocal(DateTimeOffset peerTime) => Shift(peerTime, -Volatile.Read(ref _state).Best.OffsetTicks);

    /// <summary>This computer's <paramref name="localTime"/> on the peer's clock (UTC); a <c>MinValue</c> or <c>MaxValue</c> is returned as it is.</summary>
    public DateTimeOffset ToPeer(DateTimeOffset localTime) => Shift(localTime, Volatile.Read(ref _state).Best.OffsetTicks);

    private static DateTimeOffset Shift(DateTimeOffset time, long ticks)
    {
        if (time == DateTimeOffset.MinValue || time == DateTimeOffset.MaxValue)
        {
            return time;
        }

        // Both operands are within +-DateTime.MaxValue.Ticks, so the sum cannot overflow a long.
        var shifted = Math.Clamp(time.UtcTicks + ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
        return new DateTimeOffset(shifted, TimeSpan.Zero);
    }
}
